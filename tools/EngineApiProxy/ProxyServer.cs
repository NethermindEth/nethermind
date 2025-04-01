using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
        
        // Components for state management and message queueing
        private readonly MessageQueue _messageQueue;
        private readonly PayloadTracker _payloadTracker;
        
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

            // Initialize core components
            _messageQueue = new MessageQueue(logManager);
            _payloadTracker = new PayloadTracker(logManager);

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
            
            // Start background tasks for message processing
            _ = ProcessMessageQueueAsync(cancellationToken);
            
            _logger.Info($"Engine API Proxy started on port {_config.ListenPort}");
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _webHost.StopAsync(cancellationToken);
            _logger.Info("Engine API Proxy stopped");
        }

        private async Task ProcessMessageQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!_messageQueue.IsEmpty)
                    {
                        var message = _messageQueue.DequeueNextMessage();
                        if (message != null)
                        {
                            // Process the dequeued message based on its type
                            JsonRpcResponse response;
                            switch (message.Request.Method)
                            {
                                case "engine_newPayload":
                                    response = await HandleNewPayload(message.Request);
                                    break;
                                default:
                                    response = await ForwardRequestToExecutionClient(message.Request);
                                    break;
                            }
                            
                            // Complete the message with the response
                            if (message.Request.Id != null)
                            {
                                message.CompletionTask.TrySetResult(response);
                            }
                        }
                    }
                    
                    // Add a short delay to prevent high CPU usage
                    await Task.Delay(10, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error processing message queue: {ex.Message}", ex);
                    await Task.Delay(100, cancellationToken); // Longer delay after error
                }
            }
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
                        // Queue the newPayload message and return a placeholder response
                        // The actual request will be processed by the message queue
                        Task<JsonRpcResponse> responseTask = _messageQueue.EnqueueMessage(request);
                        
                        // If this is a synchronous request, wait for processing to complete
                        // Otherwise, return a placeholder success response
                        if (request.Id != null)
                        {
                            response = await responseTask;
                        }
                        else
                        {
                            response = new JsonRpcResponse { Id = request.Id, Result = true };
                        }
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
            // Log the forkChoiceUpdated request
            _logger.Debug($"Processing engine_forkChoiceUpdated: {request}");
            
            try
            {
                // TODO: Append PayloadAttributes to the request
                
                // Forward the modified request to EC
                var response = await ForwardRequestToExecutionClient(request);
                
                // If response contains payloadId, store it for tracking
                if (response.Result is JObject resultObj && 
                    resultObj["payloadId"] != null && 
                    request.Params != null && 
                    request.Params.Count > 0 && 
                    request.Params[0] is JObject forkChoiceState &&
                    forkChoiceState["headBlockHash"] != null)
                {
                    string payloadId = resultObj["payloadId"]?.ToString() ?? string.Empty;
                    string headBlockHashStr = forkChoiceState["headBlockHash"]?.ToString() ?? string.Empty;
                    
                    if (!string.IsNullOrEmpty(payloadId) && !string.IsNullOrEmpty(headBlockHashStr))
                    {
                        var headBlockHash = new Hash256(Bytes.FromHexString(headBlockHashStr));
                        _payloadTracker.TrackPayload(headBlockHash, payloadId);
                        _logger.Debug($"Tracked payloadId {payloadId} for head block {headBlockHash}");
                    }
                }
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error handling forkChoiceUpdated: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Error handling forkChoiceUpdated: {ex.Message}");
            }
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
            // Log the getPayload request
            _logger.Debug($"Processing engine_getPayload: {request}");
            
            try
            {
                // Forward the request to EC
                var response = await ForwardRequestToExecutionClient(request);
                
                // If a payload was returned and the response was successful, handle it
                if (response.Result is JObject payloadObj && 
                    request.Params != null && 
                    request.Params.Count > 0)
                {
                    string payloadId = request.Params[0]?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(payloadId))
                    {
                        _logger.Debug($"Retrieved payload for payloadId {payloadId}");
                    }
                    
                    // TODO: Generate a synthetic engine_newPayload request from the payload
                }
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error handling getPayload: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Error handling getPayload: {ex.Message}");
            }
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