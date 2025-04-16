using System.Text;
using Nethermind.EngineApiProxy.Models;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.EngineApiProxy.Services
{
    /// <summary>
    /// Fetches block data from the execution client using JSON-RPC
    /// </summary>
    public class BlockDataFetcher(HttpClient httpClient, ILogManager logManager)
    {
        private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

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
                _logger.Info($"PR -> EL|{request.Method}|V|{requestJson}");
                var response = await _httpClient.SendAsync(requestMessage);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Error($"Failed to get block data. Status: {response.StatusCode}");
                    return null;
                }
                
                var responseJson = await response.Content.ReadAsStringAsync();
                _logger.Info($"EL -> PR|{request.Method}|V|{responseJson}");
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
    }
} 