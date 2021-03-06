// <copyright file="ExchangeSyncHelper.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace Microsoft.Teams.Apps.BookAThing.SyncService.Service
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.Identity.Client;
    using Microsoft.Teams.App.BookAThing.Common.Models.Error;
    using Microsoft.Teams.App.BookAThing.Common.Models.Response;
    using Microsoft.Teams.Apps.BookAThing.Common;
    using Microsoft.Teams.Apps.BookAThing.Common.Helpers;
    using Microsoft.Teams.Apps.BookAThing.Common.Models.TableEntities;
    using Microsoft.Teams.Apps.BookAThing.Common.Providers.Storage;
    using Newtonsoft.Json;
    using Polly;
    using Polly.Contrib.WaitAndRetry;
    using Polly.Retry;

    /// <summary>
    /// Methods for performing exchange to table storage sync operation.
    /// </summary>
    public class ExchangeSyncHelper : IExchangeSyncHelper
    {
        /// <summary>
        /// Retry policy with jitter, Reference: https://github.com/Polly-Contrib/Polly.Contrib.WaitAndRetry#new-jitter-recommendation.
        /// </summary>
        private static RetryPolicy retryPolicy = Policy.Handle<Exception>().WaitAndRetryAsync(Backoff.DecorrelatedJitterBackoffV2(TimeSpan.FromMilliseconds(1000), 2));

        /// <summary>
        /// Graph API to get building list using application token. (Replace {id} with user Id).
        /// </summary>
        private readonly string graphAPIAppFindRoomList = "/beta/places/microsoft.graph.roomlist";

        /// <summary>
        /// Graph API to get list of rooms for building(s) using application token. (Replace {id} with user Id and {buildingAlias} with comma separated building emails).
        /// </summary>
        private readonly string graphAPIAppFindRooms = "/beta/places/{buildingAlias}/microsoft.graph.roomlist/rooms";

        /// <summary>
        /// Storage provider to perform insert, update and delete operations on RoomCollection table.
        /// </summary>
        private readonly IRoomCollectionStorageProvider roomCollectionStorageProvider;

        /// <summary>
        /// Storage provider to perform delete operation on UserFavorites table.
        /// </summary>
        private readonly IFavoriteStorageProvider favoriteStorageProvider;

        /// <summary>
        /// Api helper service for making post and get calls to Graph.
        /// </summary>
        private readonly IGraphApiHelper apiHelper;

        /// <summary>
        /// Telemetry service to log events and errors.
        /// </summary>
        private readonly TelemetryClient telemetryClient;

        /// <summary>
        /// Used to get application access token.
        /// </summary>
        private readonly IConfidentialClientApplication confidentialClientApplication;

        /// <summary>
        /// Search service for searching room/building as per user input.
        /// </summary>
        private readonly ISearchService searchService;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExchangeSyncHelper"/> class.
        /// </summary>
        /// <param name="roomCollectionStorageProvider">Storage provider to perform insert, update and delete operations on RoomCollection table.</param>
        /// <param name="favoriteStorageProvider">Storage provider to perform delete operation on UserFavorites table.</param>
        /// <param name="apiHelper">Api helper service for making post and get calls to Graph.</param>
        /// <param name="telemetryClient">Telemetry service to log events and errors.</param>
        /// <param name="confidentialClientApplication">Used to get application access token.</param>
        /// <param name="searchService">Search service for searching room/building as per user input.</param>
        public ExchangeSyncHelper(IRoomCollectionStorageProvider roomCollectionStorageProvider, IFavoriteStorageProvider favoriteStorageProvider, IGraphApiHelper apiHelper, TelemetryClient telemetryClient, IConfidentialClientApplication confidentialClientApplication, ISearchService searchService)
        {
            this.searchService = searchService;
            this.roomCollectionStorageProvider = roomCollectionStorageProvider;
            this.favoriteStorageProvider = favoriteStorageProvider;
            this.apiHelper = apiHelper;
            this.telemetryClient = telemetryClient;
            this.confidentialClientApplication = confidentialClientApplication;
        }

        /// <summary>
        /// Process exchange to storage sync.
        /// </summary>
        /// <returns>A task that represents the work queued to execute.</returns>
        public async Task ExchangeToStorageExportAsync()
        {
            // 1. Get list of buildings from Microsoft Graph API.
            // 2. Create batch of 10 buildings. (Useful for parallel execution)
            // 3. For every building in batch do:
            //    - Get records from table storage matching building email id.
            //    - Get rooms from Microsoft Graph API associated with building email id.
            //    - Check which rooms got deleted by comparing rooms from Azure table storage and rooms Microsoft Graph API
            //          - Set IsDeleted flag to true in RoomCollection table for rooms which got deleted.
            //    - Update or insert rooms received from Microsoft Graph API in RoomCollection table.
            this.telemetryClient.TrackTrace("Exchange sync started");

            string token = await this.GetApplicationAccessTokenAsync().ConfigureAwait(false);
            if (token == null)
            {
                this.telemetryClient.TrackTrace("Exchange sync - Application access token is null.");
                return;
            }

            PlaceResponse buildings = new PlaceResponse();
            var httpResponseMessage = await this.apiHelper.GetAsync(this.graphAPIAppFindRoomList, token).ConfigureAwait(false);
            var content = await httpResponseMessage.Content.ReadAsStringAsync();

            if (httpResponseMessage.IsSuccessStatusCode)
            {
                buildings = JsonConvert.DeserializeObject<PlaceResponse>(content);
            }
            else
            {
                var errorResponse = JsonConvert.DeserializeObject<ErrorResponse>(content);
                this.telemetryClient.TrackTrace($"Exchange sync - Graph API failure- url: {this.graphAPIAppFindRoomList}, response-code: {errorResponse.Error.StatusCode}, response-content: {errorResponse.Error.ErrorMessage}, request-id: {errorResponse.Error.InnerError.RequestId}", SeverityLevel.Warning);
            }

            this.telemetryClient.TrackTrace($"Exchange sync - Building count: {buildings?.PlaceDetails?.Count}");

            var buildingsPerBatch = 10;
            if (buildings?.PlaceDetails?.Count > 0)
            {
                int count = (int)Math.Ceiling((double)buildings.PlaceDetails.Count / buildingsPerBatch);
                for (int i = 0; i < count; i++)
                {
                    var buildingsBatch = buildings.PlaceDetails.Skip(i * buildingsPerBatch).Take(buildingsPerBatch);
                    await Task.WhenAll(buildingsBatch.Select(building => this.ProcessBuildingAsync(building))).ConfigureAwait(false);
                }
            }
            else
            {
                this.telemetryClient.TrackTrace("Exchange sync- Buildings count is 0");
            }

            await this.searchService.InitializeAsync();
        }

        /// <summary>
        /// Process each building sync operation.
        /// </summary>
        /// <param name="building">Building object.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        private async Task ProcessBuildingAsync(PlaceInfo building)
        {
            await retryPolicy.ExecuteAsync(async () =>
            {
                string token = await this.GetApplicationAccessTokenAsync().ConfigureAwait(false);
                if (token == null)
                {
                    this.telemetryClient.TrackTrace("Exchange sync - Application access token is null.");
                    return;
                }

                var roomsFromStorage = await this.roomCollectionStorageProvider.GetAsync(building.EmailAddress).ConfigureAwait(false);

                // Get rooms from graph api (max 100 rooms returned per building).
                var rooms = new PlaceResponse();
                var httpResponseMessage = await this.apiHelper.GetAsync(this.graphAPIAppFindRooms.Replace("{buildingAlias}", building.EmailAddress, StringComparison.OrdinalIgnoreCase), token).ConfigureAwait(false);
                var content = await httpResponseMessage.Content.ReadAsStringAsync();

                if (httpResponseMessage.IsSuccessStatusCode)
                {
                    rooms = JsonConvert.DeserializeObject<PlaceResponse>(content);
                }
                else
                {
                    var errorResponse = JsonConvert.DeserializeObject<ErrorResponse>(content);
                    this.telemetryClient.TrackTrace($"Graph API failure- url: {this.graphAPIAppFindRooms}, response-code: {errorResponse.Error.StatusCode}, response-content: {errorResponse.Error.ErrorMessage}, request-id: {errorResponse.Error.InnerError.RequestId}", SeverityLevel.Warning);
                }

                this.telemetryClient.TrackTrace($"Exchange sync - Building {building.DisplayName}, rooms count: {rooms?.PlaceDetails?.Count}");

                // Delete existing rooms of building from storage.
                if (roomsFromStorage?.Count > 0)
                {
                    this.telemetryClient.TrackTrace($"Exchange sync - Building {building.DisplayName}, deleting rooms from storage");

                    // Get room email IDs which got removed from Microsoft Exchange.
                    var roomsToRemoveEmails = roomsFromStorage.Select(room => room.RowKey).Except(rooms.PlaceDetails.Select(room => room.EmailAddress));
                    var roomsToRemove = roomsFromStorage.Where(room => roomsToRemoveEmails.Contains(room.RowKey)).ToList();

                    // Set isDeleted flag to true for entity received from Azure table storage.
                    roomsToRemove.ForEach(room => room.IsDeleted = true);

                    // Update deleted rooms.
                    await this.roomCollectionStorageProvider.UpdateDeletedRoomsAsync(roomsToRemove).ConfigureAwait(false);
                }

                // Add or update rooms received from Microsoft Exchange.
                var allRooms = await this.AddOrReplaceRoomsAsync(building, rooms?.PlaceDetails).ConfigureAwait(false);

                this.telemetryClient.TrackTrace($"Exchange Sync - Building {building.DisplayName}, deleting rooms from user favorites");
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Add rooms to storage.
        /// </summary>
        /// <param name="building">Building information.</param>
        /// <param name="rooms">List of rooms.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        private async Task<List<MeetingRoomEntity>> AddOrReplaceRoomsAsync(PlaceInfo building, List<PlaceInfo> rooms)
        {
            var meetingRooms = new List<MeetingRoomEntity>();
            if (rooms != null)
            {
                meetingRooms = rooms.Select(room => new MeetingRoomEntity
                {
                    BuildingName = building.DisplayName,
                    Key = room.Id,
                    BuildingEmail = building.EmailAddress,
                    RoomName = room.DisplayName,
                    RoomEmail = room.EmailAddress,
                    IsDeleted = false,
                }).ToList();

                if (await this.roomCollectionStorageProvider.AddOrReplaceAsync(meetingRooms).ConfigureAwait(false))
                {
                    return meetingRooms;
                }
            }

            return meetingRooms;
        }

        /// <summary>
        /// Get application access token.
        /// </summary>
        /// <param name="clientId">Client Id.</param>
        /// <param name="clientSecret">Client Secret.</param>
        /// <param name="tenantId">Tenant Id.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        private async Task<string> GetApplicationAccessTokenAsync()
        {
            string oAuthTokenScope = "https://graph.microsoft.com/.default";
            string[] scopes = new string[] { oAuthTokenScope };
            var tokenResult = await this.confidentialClientApplication.AcquireTokenForClient(scopes).ExecuteAsync().ConfigureAwait(false);
            return tokenResult?.AccessToken;
        }
    }
}
