// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.Logging;

namespace Nethermind.EngineApiProxy.Utilities;

public class RequestForwarder(
    HttpClient httpClient,
    ProxyConfig config,
    ILogManager logManager)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ILogger _logger = logManager?.GetClassLogger<RequestForwarder>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly ProxyConfig _config = config ?? throw new ArgumentNullException(nameof(config));

    public virtual async Task<JsonRpcResponse> ForwardRequestToExecutionClient(JsonRpcRequest request, bool logResponse = true)
    {
        try
        {
            string requestJson = JsonSerializer.Serialize(request);
            string targetHost = _httpClient.BaseAddress?.ToString() ?? "unknown";
            _logger.Debug($"Forwarding request to EL at: {targetHost}");
            if (logResponse)
            {
                _logger.Info($"PR -> EL|{request.Method}|{requestJson}");
            }
            else
            {
                _logger.Debug($"PR -> EL|{request.Method}|{requestJson}");
            }
            StringContent content = new(requestJson, Encoding.UTF8, "application/json");

            // Create a request message instead of using PostAsync directly
            HttpRequestMessage requestMessage = new(HttpMethod.Post, "")
            {
                Content = content
            };

            if (request.OriginalHeaders is not null)
            {
                _logger.Debug($"Forwarding {request.OriginalHeaders.Count} original headers from client request");
            }
            else
            {
                _logger.Info("No original headers to forward");
            }

            HttpHeaderForwarder.AttachForwardedHeaders(requestMessage, request.OriginalHeaders);

            HttpResponseMessage response;
            try
            {
                _logger.Debug($"Sending request to EL: {request.Method}");
                response = await _httpClient.SendAsync(requestMessage);
                string? contentEncoding = null;
                foreach (string encoding in response.Content.Headers.ContentEncoding)
                {
                    contentEncoding = encoding;
                    break;
                }
                _logger.Debug($"Received response from EL: {response.StatusCode}, Content-Encoding: {contentEncoding ?? "none"}, Content-Type: {response.Content.Headers.ContentType?.ToString() ?? "unknown"}");
            }
            catch (HttpRequestException ex) when (ex.InnerException is HttpIOException ioEx)
            {
                _logger.Error($"Network IO error communicating with EL: {ioEx.Message}. This could indicate connection issues or server premature disconnect.");
                return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error: Network IO error with EL: {ioEx.Message}");
            }
            catch (HttpRequestException ex)
            {
                _logger.Error($"HTTP request error communicating with EL: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error: HTTP error with EL: {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                _logger.Error($"Request timed out after {_config.RequestTimeoutSeconds}s: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error: Request to EL timed out after {_config.RequestTimeoutSeconds}s");
            }

            string responseBody = await response.Content.ReadAsStringAsync();
            _logger.Debug($"Received response from EL at: {targetHost}");
            if (logResponse)
            {
                _logger.Info($"EL -> PR|{request.Method}|{responseBody}");
            }
            else
            {
                _logger.Debug($"EL -> PR|{request.Method}|{responseBody}");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.Error($"EL returned error: {response.StatusCode}, {responseBody}");
                return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error: EL error: {response.StatusCode}");
            }

            JsonRpcResponse? jsonRpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(responseBody);
            if (jsonRpcResponse is null)
            {
                return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, "Proxy error: Invalid response from EL");
            }

            return jsonRpcResponse;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error forwarding request to EL: {ex.Message}", ex);
            return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error: Communicating with EL: {ex.Message}");
        }
    }
}
