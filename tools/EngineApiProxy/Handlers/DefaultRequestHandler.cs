// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EngineApiProxy.Models;
using Nethermind.EngineApiProxy.Utilities;
using Nethermind.Logging;

namespace Nethermind.EngineApiProxy.Handlers;

public class DefaultRequestHandler(
    RequestForwarder requestForwarder,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly RequestForwarder _requestForwarder = requestForwarder;

    public async Task<JsonRpcResponse> HandleRequest(JsonRpcRequest request)
    {
        _logger.Info($"Processing default request for method {request.Method}");

        try
        {
            // Forward the request directly to execution client
            return await _requestForwarder.ForwardRequestToExecutionClient(request, false);
        }
        catch (Exception ex)
        {
            _logger.Error($"Error handling {request.Method}: {ex.Message}", ex);
            return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling {request.Method}: {ex.Message}");
        }
    }
}
