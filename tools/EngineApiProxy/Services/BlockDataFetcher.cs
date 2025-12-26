// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.EngineApiProxy.Models;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.EngineApiProxy.Services;

/// <summary>
/// Fetches block data from the execution and consensus clients using JSON-RPC
/// </summary>
/// <remarks>
/// Initializes a new instance of BlockDataFetcher
/// </remarks>
/// <param name="httpClient">The HttpClient for execution client communication</param>
/// <param name="consensusClient">Optional HttpClient for consensus client communication</param>
/// <param name="logManager">Log manager for creating loggers</param>
public class BlockDataFetcher(HttpClient httpClient, ILogManager logManager, HttpClient? consensusClient = null)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly HttpClient? _consensusClient = consensusClient;
    private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

    /// <summary>
    /// Gets or sets the consensus client endpoint URL
    /// </summary>
    public string? ConsensusClientEndpoint { get; set; }

    /// <summary>
    /// Fetches block data from the execution client by hash
    /// </summary>
    /// <param name="blockHash">The block hash</param>
    /// <returns>Block data as a JObject</returns>
    public virtual async Task<JObject?> GetBlockByHash(string blockHash)
    {
        _logger.Debug($"Fetching block data for hash: {blockHash}");

        try
        {
            // Create JSON-RPC request
            var request = new JsonRpcRequest(
                "eth_getBlockByHash",
                [blockHash, true], // Include transaction objects
                Guid.NewGuid().ToString());

            // Send request to EC
            var requestJson = JsonConvert.SerializeObject(request);
            var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");

            // Create a request message instead of using PostAsync directly
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "")
            {
                Content = httpContent
            };

            bool authHeaderAdded = false;

            // Copy all authorization headers from the HttpClient
            if (_httpClient.DefaultRequestHeaders.Contains("Authorization"))
            {
                var authHeader = _httpClient.DefaultRequestHeaders.GetValues("Authorization").FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader))
                {
                    _logger.Debug($"Adding Authorization header to block data fetch request for hash: {blockHash}");
                    requestMessage.Headers.TryAddWithoutValidation("Authorization", authHeader);
                    authHeaderAdded = true;
                }
            }

            if (!authHeaderAdded)
            {
                _logger.Warn($"No Authorization header available for block data fetch request for hash: {blockHash}");
            }

            _logger.Debug($"Sending block data fetch request with Authorization header: {authHeaderAdded}");
            _logger.Debug($"PR -> EL|{request.Method}|V|{requestJson}");
            var response = await _httpClient.SendAsync(requestMessage);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Error($"Failed to get block data. Status: {response.StatusCode}");
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            _logger.Debug($"EL -> PR|{request.Method}|V|{responseJson}");
            var jsonResponse = JsonConvert.DeserializeObject<JsonRpcResponse>(responseJson);

            if (jsonResponse?.Result is JObject blockData)
            {
                _logger.Debug($"Successfully fetched block data for hash: {blockHash}");
                return blockData;
            }

            _logger.Error($"Invalid response format for block data: {responseJson}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching block data: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Fetches the latest block from the execution client
    /// </summary>
    /// <returns>Latest block data as a JObject</returns>
    public virtual async Task<JObject?> GetLatestBlock()
    {
        _logger.Debug("Fetching latest block data");

        try
        {
            // Create JSON-RPC request
            var request = new JsonRpcRequest(
                "eth_getBlockByNumber",
                ["latest", true], // Include transaction objects
                Guid.NewGuid().ToString());

            // Send request to EC
            var requestJson = JsonConvert.SerializeObject(request);
            var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");

            // Create a request message instead of using PostAsync directly
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "")
            {
                Content = httpContent
            };

            bool authHeaderAdded = false;

            // Copy all authorization headers from the HttpClient
            if (_httpClient.DefaultRequestHeaders.Contains("Authorization"))
            {
                var authHeader = _httpClient.DefaultRequestHeaders.GetValues("Authorization").FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader))
                {
                    _logger.Debug("Adding Authorization header to latest block fetch request");
                    requestMessage.Headers.TryAddWithoutValidation("Authorization", authHeader);
                    authHeaderAdded = true;
                }
            }

            if (!authHeaderAdded)
            {
                _logger.Warn("No Authorization header available for latest block fetch request");
            }

            _logger.Debug($"Sending latest block fetch request with Authorization header: {authHeaderAdded}");
            var response = await _httpClient.SendAsync(requestMessage);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Error($"Failed to get latest block data. Status: {response.StatusCode}");
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var jsonResponse = JsonConvert.DeserializeObject<JsonRpcResponse>(responseJson);

            if (jsonResponse?.Result is JObject blockData)
            {
                _logger.Debug("Successfully fetched latest block data");
                return blockData;
            }

            _logger.Error($"Invalid response format for latest block data: {responseJson}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching latest block data: {ex.Message}", ex);
            return null;
        }
    }


    /// <summary>
    /// Gets the head beacon block header from the consensus client
    /// </summary>
    /// <returns>Head beacon block header data or null if request fails</returns>
    public virtual async Task<JObject?> GetBeaconBlockHeader()
    {
        if (_consensusClient == null)
        {
            _logger.Error("Cannot get beacon block header: no CL client configured");
            return null;
        }

        _logger.Debug("Fetching head beacon block header");

        try
        {
            var requestMessage = new HttpRequestMessage(HttpMethod.Get, "/eth/v1/beacon/headers/head");

            _logger.Info("PR -> CL|B|/eth/v1/beacon/headers/head");
            var response = await _consensusClient.SendAsync(requestMessage);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Error($"Failed to get beacon block header. Status: {response.StatusCode}");
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            _logger.Info($"CL -> PR|B|/eth/v1/beacon/headers/head|{responseJson}");

            var responseObj = JObject.Parse(responseJson);
            _logger.Debug("Successfully received head beacon block header");

            return responseObj;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching beacon block header: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Gets a specific beacon block from the consensus client
    /// </summary>
    /// <param name="blockId">Block identifier (root hash or slot number)</param>
    /// <returns>Beacon block data or null if request fails</returns>
    public virtual async Task<JObject?> GetBeaconBlock(string blockId)
    {
        if (_consensusClient == null)
        {
            _logger.Error("Cannot get beacon block: no CL client configured");
            return null;
        }

        _logger.Debug($"Fetching beacon block with ID: {blockId}");

        try
        {
            var requestMessage = new HttpRequestMessage(HttpMethod.Get, $"/eth/v1/beacon/blocks/{blockId}");

            _logger.Info($"PR -> CL|B|/eth/v1/beacon/blocks/{blockId}");
            var response = await _consensusClient.SendAsync(requestMessage);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Error($"Failed to get beacon block. Status: {response.StatusCode}");
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            _logger.Info($"CL -> PR|B|/eth/v1/beacon/blocks/{blockId}|{responseJson.Substring(0, Math.Min(200, responseJson.Length))}...");

            var responseObj = JObject.Parse(responseJson);
            _logger.Debug($"Successfully received beacon block with ID: {blockId}");

            return responseObj;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching beacon block: {ex.Message}", ex);
            return null;
        }
    }
}
