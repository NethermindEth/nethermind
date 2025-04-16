using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.EngineApiProxy.Utilities;
using Nethermind.Logging;
using Newtonsoft.Json.Linq;

namespace Nethermind.EngineApiProxy.Handlers
{
    public class GetPayloadHandler(
        ProxyConfig config,
        RequestForwarder requestForwarder,
        PayloadTracker payloadTracker,
        ILogManager logManager)
    {
        private readonly ProxyConfig _config = config ?? throw new ArgumentNullException(nameof(config));
        private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly RequestForwarder _requestForwarder = requestForwarder ?? throw new ArgumentNullException(nameof(requestForwarder));
        private readonly PayloadTracker _payloadTracker = payloadTracker ?? throw new ArgumentNullException(nameof(payloadTracker));

        public async Task<JsonRpcResponse> HandleRequest(JsonRpcRequest request)
        {
            // Log the getPayload request
            _logger.Info($"Processing {request.Method}: {request}");
            
            try
            {
                // Forward the request to EC
                var response = await _requestForwarder.ForwardRequestToExecutionClient(request);
                
                // If a payload was returned and the response was successful, handle it
                if (response.Result is JObject payloadObj && 
                    request.Params != null && 
                    request.Params.Count > 0)
                {
                    string payloadId = request.Params[0]?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(payloadId))
                    {
                        _logger.Debug($"Retrieved payload for payloadId {payloadId}");
                        
                        // For merged validation mode, track additional information about the payload
                        if (_config.ValidationMode == ValidationMode.Merged)
                        {
                            _logger.Debug($"Merged validation mode: Processing payload for payloadId {payloadId}");
                            
                            // Extract block hash from the payload if available
                            if (payloadObj["blockHash"] != null)
                            {
                                string blockHash = payloadObj["blockHash"]?.ToString() ?? string.Empty;
                                _logger.Debug($"Merged validation: Retrieved blockHash {blockHash} for payloadId {payloadId}");
                            }
                        }
                    }
                }
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error handling {request.Method}: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling {request.Method}: {ex.Message}");
            }
        }
    }
} 