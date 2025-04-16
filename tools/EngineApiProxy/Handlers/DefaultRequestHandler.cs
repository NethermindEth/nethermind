using Nethermind.EngineApiProxy.Models;
using Nethermind.EngineApiProxy.Utilities;
using Nethermind.Logging;
using System;
using System.Threading.Tasks;

namespace Nethermind.EngineApiProxy.Handlers
{
    public class DefaultRequestHandler
    {
        private readonly ILogger _logger;
        private readonly RequestForwarder _requestForwarder;

        public DefaultRequestHandler(
            RequestForwarder requestForwarder,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _requestForwarder = requestForwarder ?? throw new ArgumentNullException(nameof(requestForwarder));
        }

        public async Task<JsonRpcResponse> HandleRequest(JsonRpcRequest request)
        {
            _logger.Debug($"Processing default request for method {request.Method}");
            
            try
            {
                // Forward the request directly to execution client
                return await _requestForwarder.ForwardRequestToExecutionClient(request);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error handling {request.Method}: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling {request.Method}: {ex.Message}");
            }
        }
    }
} 