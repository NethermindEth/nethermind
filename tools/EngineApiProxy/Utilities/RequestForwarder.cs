using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.Logging;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.EngineApiProxy.Utilities
{
    public class RequestForwarder
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly ProxyConfig _config;

        public RequestForwarder(
            HttpClient httpClient,
            ProxyConfig config,
            ILogManager logManager)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public virtual async Task<JsonRpcResponse> ForwardRequestToExecutionClient(JsonRpcRequest request)
        {
            try
            {
                string requestJson = JsonConvert.SerializeObject(request);
                string targetHost = _httpClient.BaseAddress?.ToString() ?? "unknown";
                _logger.Debug($"Forwarding request to EL at: {targetHost}");
                _logger.Info($"PR -> EL|{request.Method}|{requestJson}");
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                
                // Create a request message instead of using PostAsync directly
                var requestMessage = new HttpRequestMessage(HttpMethod.Post, "")
                {
                    Content = content
                };
                
                // Copy all original headers from the client request
                if (request.OriginalHeaders != null)
                {
                    _logger.Debug($"Forwarding {request.OriginalHeaders.Count} original headers from client request");
                    
                    // Log the presence of Authorization header in original headers
                    if (request.OriginalHeaders.TryGetValue("Authorization", out var origAuthHeader))
                    {
                        _logger.Trace($"Found Authorization header in original request headers: {origAuthHeader.Substring(0, Math.Min(10, origAuthHeader.Length))}...");
                    }
                    else
                    {
                        _logger.Debug("No Authorization header found in original request headers");
                    }
                    
                    // Special handling for Authorization header
                    if (request.OriginalHeaders.TryGetValue("Authorization", out var authHeader))
                    {
                        requestMessage.Headers.TryAddWithoutValidation("Authorization", authHeader);
                        _logger.Debug("Added Authorization header from original request headers");
                    }
                    
                    foreach (var header in request.OriginalHeaders)
                    {
                        // Skip content-related headers that will be set by HttpClient
                        // Also skip Authorization which was handled separately
                        if (!header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && 
                            !string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                        {
                            requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            _logger.Trace($"Added header: {header.Key}");
                        }
                        else if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Trace($"Skipped content header: {header.Key}");
                        }
                    }
                }
                else
                {
                    _logger.Info("No original headers to forward");
                }
                
                // Always check the HttpClient's DefaultRequestHeaders for Authorization as a fallback
                if (!requestMessage.Headers.Contains("Authorization") && _httpClient.DefaultRequestHeaders.Contains("Authorization"))
                {
                    var authHeader = _httpClient.DefaultRequestHeaders.GetValues("Authorization").FirstOrDefault();
                    if (!string.IsNullOrEmpty(authHeader))
                    {
                        _logger.Debug("Adding Authorization header from HttpClient DefaultRequestHeaders as fallback");
                        requestMessage.Headers.TryAddWithoutValidation("Authorization", authHeader);
                    }
                }
                
                var response = await _httpClient.SendAsync(requestMessage);
                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.Debug($"Received response from EL at: {targetHost}");
                _logger.Info($"EL -> PR|{request.Method}|{responseBody}");
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Error($"EL returned error: {response.StatusCode}, {responseBody}");
                    return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"EL error: {response.StatusCode}");
                }
                
                var jsonRpcResponse = JsonConvert.DeserializeObject<JsonRpcResponse>(responseBody);
                if (jsonRpcResponse == null)
                {
                    return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, "Proxy error: Invalid response from EL");
                }
                
                return jsonRpcResponse;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error forwarding request to EL: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error communicating with EL: {ex.Message}");
            }
        }
    }
} 