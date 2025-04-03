using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.EngineApiProxy.Services
{
    /// <summary>
    /// Orchestrates the flow of requests for block validation
    /// </summary>
    public class RequestOrchestrator
    {
        private readonly BlockDataFetcher _blockFetcher;
        private readonly PayloadAttributesGenerator _attributesGenerator;
        private readonly MessageQueue _messageQueue;
        private readonly PayloadTracker _payloadTracker;
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly ProxyConfig _config;
        
        public RequestOrchestrator(
            HttpClient httpClient, 
            BlockDataFetcher blockFetcher,
            PayloadAttributesGenerator attributesGenerator,
            MessageQueue messageQueue,
            PayloadTracker payloadTracker,
            ProxyConfig config,
            ILogManager logManager)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _blockFetcher = blockFetcher ?? throw new ArgumentNullException(nameof(blockFetcher));
            _attributesGenerator = attributesGenerator ?? throw new ArgumentNullException(nameof(attributesGenerator));
            _messageQueue = messageQueue ?? throw new ArgumentNullException(nameof(messageQueue));
            _payloadTracker = payloadTracker ?? throw new ArgumentNullException(nameof(payloadTracker));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        /// <summary>
        /// Handles the fork choice updated validation flow
        /// </summary>
        /// <param name="originalRequest">The original FCU request</param>
        /// <param name="headBlockHash">The head block hash from the FCU request</param>
        /// <returns>The payloadId from the FCU response</returns>
        public async Task<string> HandleFCUWithValidation(JsonRpcRequest originalRequest, string headBlockHash)
        {
            _logger.Debug($"Starting validation flow for head block: {headBlockHash}");
            
            try
            {
                // 1. Get the head block data
                var blockData = await _blockFetcher.GetBlockByHash(headBlockHash);
                if (blockData == null)
                {
                    throw new InvalidOperationException($"Failed to fetch block data for hash: {headBlockHash}");
                }
                
                // 2. Generate payload attributes
                var payloadAttributes = _attributesGenerator.GeneratePayloadAttributes(blockData);
                
                // 3. Clone the request and add payload attributes
                var modifiedRequest = CloneRequest(originalRequest);
                if (modifiedRequest.Params == null)
                {
                    modifiedRequest.Params = new JArray();
                }
                
                // Ensure the first parameter (fork choice state) exists
                if (modifiedRequest.Params.Count == 0)
                {
                    modifiedRequest.Params.Add(new JObject
                    {
                        ["headBlockHash"] = headBlockHash,
                        ["finalizedBlockHash"] = headBlockHash, // Use same hash as finalized for simplicity
                        ["safeBlockHash"] = headBlockHash // Use same hash as safe for simplicity
                    });
                }
                
                // Add or replace payload attributes
                if (modifiedRequest.Params.Count == 1)
                {
                    modifiedRequest.Params.Add(payloadAttributes);
                }
                else
                {
                    modifiedRequest.Params[1] = payloadAttributes;
                }
                
                // 4. Send modified FCU to execution client
                _logger.Debug($"Sending modified FCU with payload attributes: {modifiedRequest}");
                var fcuResponse = await SendJsonRpcRequest(modifiedRequest);
                
                // 5. Extract payload ID from response
                if (fcuResponse.Result is JObject resultObj && resultObj["payloadId"] != null)
                {
                    string payloadId = resultObj["payloadId"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(payloadId))
                    {
                        throw new InvalidOperationException("Received empty payloadId from FCU response");
                    }
                    
                    _logger.Info($"Received payloadId: {payloadId} for head block: {headBlockHash}");
                    
                    // 6. Wait a short time for EL to prepare the payload
                    await Task.Delay(500);
                    
                    // 7. Get the payload using engine_getPayload
                    await GetAndProcessPayload(payloadId);
                    
                    return payloadId;
                }
                else
                {
                    throw new InvalidOperationException("FCU response did not contain a payloadId");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error in FCU validation flow: {ex.Message}", ex);
                throw;
            }
        }
        
        /// <summary>
        /// Gets the payload for a given payload ID and processes it
        /// </summary>
        /// <param name="payloadId">The payload ID to get</param>
        /// <returns>The get payload response</returns>
        private async Task<JsonRpcResponse> GetAndProcessPayload(string payloadId)
        {
            try
            {
                // Create getPayload request
                var getPayloadRequest = new JsonRpcRequest(
                    "engine_getPayload",
                    new JArray { payloadId },
                    Guid.NewGuid().ToString());
                
                // Send request to get the payload
                _logger.Debug($"Getting payload for payloadId: {payloadId}");
                var payloadResponse = await SendJsonRpcRequest(getPayloadRequest);
                
                if (payloadResponse.Result is JObject payload)
                {
                    // Create newPayload request from the payload
                    _logger.Debug($"Creating newPayload request from payload: {payload}");
                    var newPayloadRequest = CreateNewPayloadRequest(payload);
                    
                    // Send newPayload request
                    _logger.Debug($"Sending synthetic newPayload request: {newPayloadRequest}");
                    var newPayloadResponse = await SendJsonRpcRequest(newPayloadRequest);
                    
                    _logger.Info($"Completed synthetic block validation flow for payloadId: {payloadId}");
                    return payloadResponse;
                }
                else
                {
                    throw new InvalidOperationException($"Invalid payload response format for payloadId: {payloadId}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error getting and processing payload: {ex.Message}", ex);
                throw;
            }
        }
        
        /// <summary>
        /// Creates a newPayload request from a getPayload response
        /// </summary>
        /// <param name="payload">The payload from getPayload</param>
        /// <returns>A newPayload request</returns>
        private JsonRpcRequest CreateNewPayloadRequest(JObject payload)
        {
            // Extract the execution payload from the getPayload response
            var blockValue = payload.DeepClone() as JObject;
            
            if (blockValue == null)
            {
                throw new InvalidOperationException("Failed to create execution payload from getPayload response");
            }
            
            // Create newPayload request with the execution payload as the first parameter
            return new JsonRpcRequest(
                "engine_newPayload",
                new JArray { blockValue },
                Guid.NewGuid().ToString());
        }
        
        /// <summary>
        /// Sends a JSON-RPC request to the execution client
        /// </summary>
        /// <param name="request">The request to send</param>
        /// <returns>The response from the execution client</returns>
        private async Task<JsonRpcResponse> SendJsonRpcRequest(JsonRpcRequest request)
        {
            try
            {
                // Serialize request
                var requestJson = JsonConvert.SerializeObject(request);
                var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
                
                // Send request
                var response = await _httpClient.PostAsync("", httpContent);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"HTTP request failed with status code: {response.StatusCode}");
                }
                
                // Parse response
                var responseJson = await response.Content.ReadAsStringAsync();
                _logger.Debug($"Received response: {responseJson}");
                
                var jsonRpcResponse = JsonConvert.DeserializeObject<JsonRpcResponse>(responseJson);
                if (jsonRpcResponse == null)
                {
                    throw new InvalidOperationException("Failed to deserialize JSON-RPC response");
                }
                
                return jsonRpcResponse;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error sending JSON-RPC request: {ex.Message}", ex);
                throw;
            }
        }
        
        /// <summary>
        /// Clones a JSON-RPC request
        /// </summary>
        /// <param name="request">The request to clone</param>
        /// <returns>A clone of the request</returns>
        private JsonRpcRequest CloneRequest(JsonRpcRequest request)
        {
            return new JsonRpcRequest(
                request.Method,
                request.Params?.DeepClone() as JArray,
                request.Id)
            {
                OriginalHeaders = request.OriginalHeaders != null 
                    ? new Dictionary<string, string>(request.OriginalHeaders) 
                    : null
            };
        }
    }
} 