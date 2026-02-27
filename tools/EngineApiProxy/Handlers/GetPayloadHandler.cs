// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.EngineApiProxy.Utilities;
using Nethermind.Logging;

namespace Nethermind.EngineApiProxy.Handlers;

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

            return response;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error handling {request.Method}: {ex.Message}", ex);
            return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling {request.Method}: {ex.Message}");
        }
    }
}
