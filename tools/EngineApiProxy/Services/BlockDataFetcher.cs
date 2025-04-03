using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.EngineApiProxy.Models;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.EngineApiProxy.Services
{
    /// <summary>
    /// Fetches block data from the execution client using JSON-RPC
    /// </summary>
    public class BlockDataFetcher
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        
        public BlockDataFetcher(HttpClient httpClient, ILogManager logManager)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        /// <summary>
        /// Fetches block data from the execution client by hash
        /// </summary>
        /// <param name="blockHash">The block hash</param>
        /// <returns>Block data as a JObject</returns>
        public async Task<JObject?> GetBlockByHash(string blockHash)
        {
            _logger.Debug($"Fetching block data for hash: {blockHash}");
            
            try
            {
                // Create JSON-RPC request
                var request = new JsonRpcRequest(
                    "eth_getBlockByHash",
                    new JArray { blockHash, true }, // Include transaction objects
                    Guid.NewGuid().ToString());
                
                // Send request to EC
                var requestJson = JsonConvert.SerializeObject(request);
                var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync("", httpContent);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Error($"Failed to get block data. Status: {response.StatusCode}");
                    return null;
                }
                
                var responseJson = await response.Content.ReadAsStringAsync();
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
        public async Task<JObject?> GetLatestBlock()
        {
            _logger.Debug("Fetching latest block data");
            
            try
            {
                // Create JSON-RPC request
                var request = new JsonRpcRequest(
                    "eth_getBlockByNumber",
                    new JArray { "latest", true }, // Include transaction objects
                    Guid.NewGuid().ToString());
                
                // Send request to EC
                var requestJson = JsonConvert.SerializeObject(request);
                var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync("", httpContent);
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
    }
} 