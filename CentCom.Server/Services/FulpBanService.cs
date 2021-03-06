﻿using CentCom.Common.Models;
using CentCom.Server.Exceptions;
using CentCom.Server.Extensions;
using Extensions;
using Microsoft.Extensions.Logging;
using RestSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CentCom.Server.Services
{
    public class FulpBanService
    {
        private IRestClient _client;
        ILogger _logger;
        private const string BASE_URL = "https://api.fulp.gg/";
        private const int RECORDS_PER_PAGE = 50;
        private static BanSource _banSource = new BanSource() { Name = "fulp" };
        

        public FulpBanService(ILogger<FulpBanService> logger)
        {
            _logger = logger;
            _client = new RestClient(BASE_URL);
        }

        public async Task<IEnumerable<Ban>> GetBansAsync(int page = 1)
        {
            var request = new RestRequest($"bans/{RECORDS_PER_PAGE}/{page}", Method.GET, DataFormat.Json);
            var response = await _client.ExecuteAsync(request);

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                _logger.LogError($"Fulpstation website returned a non-200 HTTP response code: {response.StatusCode}, aborting parse.");
                throw new BanSourceUnavailableException($"Fulpstation website returned a non-200 HTTP response code: {response.StatusCode}, aborting parse.");
            }

            var toReturn = new List<Ban>();
            var content = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(response.Content);
            foreach (var ban in content["value"].GetProperty("bans").EnumerateArray())
            {
                // Get ban
                var toAdd = new Ban()
                {
                    BannedOn = DateTime.Parse(ban.GetProperty("banApplyTime").GetString()),
                    BannedBy = ban.GetProperty("adminCkey").GetString(),
                    BanType = ban.GetProperty("role")[0].GetString().ToLower() == "server" ? BanType.Server : BanType.Job,
                    Expires = ban.GetProperty("banExpireTime").GetString() == null ? (DateTime?)null : DateTime.Parse(ban.GetProperty("banExpireTime").GetString()),
                    CKey = ban.GetProperty("bannedCkey").GetString(),
                    Reason = ban.GetProperty("reason").GetString(),
                    SourceNavigation = _banSource
                };

                // Add jobs if relevant
                if (toAdd.BanType == BanType.Job)
                {
                    toAdd.AddJobRange(ban.GetProperty("role").EnumerateArray().Select(x => x.GetString()));
                }

                // Specify UTC
                toAdd.BannedOn = DateTime.SpecifyKind(toAdd.BannedOn, DateTimeKind.Utc);
                if (toAdd.Expires.HasValue)
                {
                    toAdd.Expires = DateTime.SpecifyKind(toAdd.Expires.Value, DateTimeKind.Utc);
                }

                toReturn.Add(toAdd);
            }

            return toReturn;
        }

        public async Task<IEnumerable<Ban>> GetBansBatchedAsync(int startPage = 1, int pages = -1)
        {
            var maxPages = await GetNumberOfPagesAsync();
            var range = Enumerable.Range(startPage, pages != -1 ? Math.Min(startPage + pages, maxPages) : maxPages);
            var toReturn = new ConcurrentBag<Ban>();
            await range.AsyncParallelForEach(async page =>
            {
                foreach (var b in await GetBansAsync(page))
                {
                    toReturn.Add(b);
                }
            }, 6);
            return toReturn;
        }

        public async Task<int> GetNumberOfPagesAsync()
        {
            var request = new RestRequest($"bans/{RECORDS_PER_PAGE}/1", Method.GET, DataFormat.Json);
            var result = await _client.ExecuteAsync(request);

            if (result.StatusCode != System.Net.HttpStatusCode.OK)
            {
                throw new Exception($"Unexpected non-200 status code [{result.StatusCode}] when trying to retrieve number of ban pages for Fulpstation");
            }

            var content = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result.Content);
            if (content["value"].TryGetProperty("lastPage", out var lastpage))
            {
                return lastpage.GetInt32();
            }
            else
            {
                throw new Exception("Failed to find the last page number in the response from Fulpstation's API");
            }
        }
    }
}
