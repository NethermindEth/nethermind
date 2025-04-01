using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core.Crypto;
using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.EngineApiProxy
{
    public class ProxyServer
    {
        private readonly ProxyConfig _config;
        private readonly ILogger _logger;
        private readonly IWebHost _webHost;
        private readonly HttpClient _httpClient;
        
        // Queue for delayed/intercepted messages
        private readonly ConcurrentQueue<(JsonRpcRequest Request, TaskCompletionSource<JsonRpcResponse> ResponseTask)> _delayedMessages = new();
        
        // Storage for PayloadIDs
        private readonly ConcurrentDictionary<Hash256, string> _payloadIds = new();

        public ProxyServer(ProxyConfig config, ILogManager logManager)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
            // Validate configuration
            if (string.IsNullOrWhiteSpace(_config.ExecutionClientEndpoint))
            {
                throw new ArgumentException("Execution client endpoint must be provided", nameof(config));
            }
            
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_config.ExecutionClientEndpoint)
            };

            _webHost = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.Listen(IPAddress.Any, _config.ListenPort);
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton(this);
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/", HandleJsonRpcRequest);
                    });
                })
                .Build();
            
            _logger.Info($"Proxy configured with {_config}");
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _webHost.StartAsync(cancellationToken);
            _logger.Info($"Engine API Proxy started on port {_config.ListenPort}");
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _webHost.StopAsync(cancellationToken);
            _logger.Info("Engine API Proxy stopped");
        }

        private async Task HandleJsonRpcRequest(HttpContext context)
        {
            string requestBody;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
            {
                requestBody = await reader.ReadToEndAsync();
            }
            
            _logger.Debug($"Received request: {requestBody}");
            
            JsonRpcRequest? request;
            try
            {
                request = JsonConvert.DeserializeObject<JsonRpcRequest>(requestBody);
                if (request == null)
                {
                    await SendErrorResponse(context, 400, "Invalid JSON-RPC request");
                    return;
                }
                
                _logger.Debug($"Processing request: {request}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error deserializing request: {ex.Message}", ex);
                await SendErrorResponse(context, 400, "Failed to parse JSON-RPC request");
                return;
            }
            
            JsonRpcResponse response;
            
            try
            {
                // Process the request based on method
                switch (request.Method)
                {
                    case "engine_forkChoiceUpdated":
                        response = await HandleForkChoiceUpdated(request);
                        break;
                        
                    case "engine_newPayload":
                        response = await HandleNewPayload(request);
                        break;
                        
                    case "engine_getPayload":
                        response = await HandleGetPayload(request);
                        break;
                        
                    default:
                        // Forward any other methods directly to EC
                        response = await ForwardRequestToExecutionClient(request);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error processing request: {ex.Message}", ex);
                response = JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Internal error: {ex.Message}");
            }
            
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(response));
        }

        private async Task<JsonRpcResponse> HandleForkChoiceUpdated(JsonRpcRequest request)
        {
            // This is a placeholder implementation - will be expanded in the ForkChoiceUpdatedHandler
            _logger.Debug($"Processing engine_forkChoiceUpdated: {request}");
            
            // TODO: Append PayloadAttributes and implement full handler
            return await ForwardRequestToExecutionClient(request);
        }

        private async Task<JsonRpcResponse> HandleNewPayload(JsonRpcRequest request)
        {
            // This is a placeholder implementation - will be expanded in the NewPayloadHandler
            _logger.Debug($"Processing engine_newPayload: {request}");
            
            // TODO: Implement interception logic
            return await ForwardRequestToExecutionClient(request);
        }

        private async Task<JsonRpcResponse> HandleGetPayload(JsonRpcRequest request)
        {
            // This is a placeholder implementation - will be expanded in the GetPayloadHandler
            _logger.Debug($"Processing engine_getPayload: {request}");
            
            // TODO: Implement full handler
            return await ForwardRequestToExecutionClient(request);
        }

        private async Task<JsonRpcResponse> ForwardRequestToExecutionClient(JsonRpcRequest request)
        {
            try
            {
                string requestJson = JsonConvert.SerializeObject(request);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync("", content);
                string responseBody = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Error($"EC returned error: {response.StatusCode}, {responseBody}");
                    return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"EC error: {response.StatusCode}");
                }
                
                var jsonRpcResponse = JsonConvert.DeserializeObject<JsonRpcResponse>(responseBody);
                if (jsonRpcResponse == null)
                {
                    return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, "Invalid response from EC");
                }
                
                return jsonRpcResponse;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error forwarding request to EC: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Error communicating with EC: {ex.Message}");
            }
        }

        private async Task SendErrorResponse(HttpContext context, int statusCode, string message)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            
            var error = new { error = message };
            await context.Response.WriteAsync(JsonConvert.SerializeObject(error));
        }
    }
} 