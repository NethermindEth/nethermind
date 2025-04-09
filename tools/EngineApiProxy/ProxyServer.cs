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
using Nethermind.EngineApiProxy.Services;
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
        
        // New components for validation flow
        private readonly BlockDataFetcher _blockDataFetcher;
        private readonly PayloadAttributesGenerator _payloadAttributesGenerator;
        private readonly RequestOrchestrator _requestOrchestrator;
        
        public ProxyServer(ProxyConfig config, ILogManager logManager)
        {
            Console.WriteLine("Application starting...");
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            // Add this line to ensure console output
            Console.WriteLine($"Logger initialized with level: {_config.LogLevel}");
            
            // Duplicate logger messages to console to ensure visibility
            _logger.Info("Setting up logging...");
            
            Console.WriteLine($"Engine API Proxy initializing with config: {_config}");
            
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
            
            // Initialize new components for validation flow
            _blockDataFetcher = new BlockDataFetcher(_httpClient, logManager);
            _payloadAttributesGenerator = new PayloadAttributesGenerator(config, logManager);
            _requestOrchestrator = new RequestOrchestrator(
                _httpClient, 
                _blockDataFetcher,
                _payloadAttributesGenerator,
                _messageQueue,
                _payloadTracker,
                config,
                logManager);

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
            // Exit with Ctrl+C
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
                            JsonRpcResponse response = await HandleRequest(message.Request);
                            
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
            
            string sourceIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            string sourceHost = context.Request.Headers.ContainsKey("Host") ? context.Request.Headers["Host"].ToString() : "unknown";
            _logger.Info($"CL -> PR (Source IP: {sourceIp}, Headers Host: {sourceHost}): {requestBody}");
            
            JsonRpcRequest? request;
            try
            {
                request = JsonConvert.DeserializeObject<JsonRpcRequest>(requestBody);
                if (request == null)
                {
                    await SendErrorResponse(context, 400, "Invalid JSON-RPC request");
                    return;
                }
                
                // Store the original request headers for forwarding
                request.OriginalHeaders = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());
                
                // Check if there's an Authorization header and set it on the HttpClient for all future requests
                if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
                {
                    _logger.Trace("Found Authorization header in client request, storing for future internal requests");
                    _httpClient.DefaultRequestHeaders.Remove("Authorization");
                    _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader.ToString());
                }
                
                _logger.Trace($"Processing request: {request}");
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
                    case "engine_forkChoiceUpdatedV2":
                    case "engine_forkchoiceUpdatedV3":
                    case "engine_forkChoiceUpdatedV4":
                        response = await HandleForkChoiceUpdated(request);
                        
                        break;
                        
                    case "engine_newPayload":
                    case "engine_newPayloadV2":
                    case "engine_newPayloadV3":
                        // Queue the newPayload message and return a placeholder response
                        // The actual request will be processed by the message queue
                        // Queue the newPayload message. No needs to to queue other requests since they will be send bt CL after the newPayload is processed
                        Task<JsonRpcResponse> responseTask = _messageQueue.EnqueueMessage(request);

                        // Always wait for the actual response from execution client
                        response = await responseTask;
                        
                        // Log the response received from the message queue
                        _logger.Debug($"Received response from message queue for {request.Method}: {JsonConvert.SerializeObject(response)}");
                        
                        // response = await HandleNewPayload(request);
                        break;
                        
                    case "engine_getPayloadV3":
                    case "engine_getPayloadV2":
                        response = await HandleGetPayload(request);
                        break;
                        
                    default:
                        // Forward any other methods directly to EC
                        response = await HandleRequest(request);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error processing request: {ex.Message}", ex);
                response = JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Engine API Proxy error: {ex.Message}");
            }
            
            context.Response.ContentType = "application/json";
            string destinationIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            string destinationHost = context.Request.Headers.ContainsKey("Host") ? context.Request.Headers["Host"].ToString() : "unknown";
            _logger.Info($"PR -> CL (Destination IP: {destinationIp}, Headers Host: {destinationHost}, Method: {request.Method}): {JsonConvert.SerializeObject(response)}");
            await context.Response.WriteAsync(JsonConvert.SerializeObject(response));
        }

        private async Task<JsonRpcResponse> HandleForkChoiceUpdated(JsonRpcRequest request)
        {
            // Log the forkChoiceUpdated request
            _logger.Info($"--------------------------------");
            _logger.Debug($"Processing engine_forkChoiceUpdated: {request}");
            
            try
            {
                // Check if we should validate this block
                bool shouldValidate = _config.ValidateAllBlocks && 
                                      request.Params != null && 
                                      request.Params.Count > 0 &&
                                      (request.Params.Count == 1 || 
                                       request.Params[1] == null || 
                                       (request.Params[1] is Newtonsoft.Json.Linq.JValue jv && jv.Type == Newtonsoft.Json.Linq.JTokenType.Null) ||
                                       (request.Params[1]?.Type == Newtonsoft.Json.Linq.JTokenType.Null)) &&
                                      !(request.Params.Count > 1 && request.Params[1] is JObject);
                
                // Add detailed logging to show validation decision
                if (_config.ValidateAllBlocks) 
                {
                    _logger.Debug($"ValidateAllBlocks is enabled, params count: {request.Params?.Count}, second param type: {(request.Params?.Count > 1 ? request.Params[1]?.GetType().Name : "none")}");
                    
                    if (request.Params?.Count > 1 && request.Params[1] is JObject)
                    {
                        _logger.Debug("Skipping validation because request already contains payload attributes");
                    }
                    
                    _logger.Info($"shouldValidate evaluated to: {shouldValidate}");
                }
                
                if (shouldValidate)
                {
                    _logger.Debug("Validation enabled, pausing message queue");
                    _messageQueue.PauseProcessing();
                    
                    try
                    {
                        // Get the head block hash
                        if (request.Params?[0] is JObject forkChoiceState && 
                            forkChoiceState["headBlockHash"] != null)
                        {
                            string headBlockHashStr = forkChoiceState["headBlockHash"]?.ToString() ?? string.Empty;
                            
                            if (!string.IsNullOrEmpty(headBlockHashStr))
                            {
                                _logger.Info($"Starting validation flow for head block: {headBlockHashStr}");
                                
                                try
                                {
                                    // Use the orchestrator to handle the validation flow
                                    string payloadId = await _requestOrchestrator.HandleFCUWithValidation(request, headBlockHashStr);
                                    
                                    // Instead of creating a synthetic response with payloadId,
                                    // forward the original request to EL and return that response to CL
                                    _logger.Info($"Validation flow for payloadId {payloadId} completed successfully, forwarding original request to EL for actual response");
                                    return await ForwardRequestToExecutionClient(request);
                                }
                                catch (Exception ex)
                                {
                                    // If the validation flow fails due to unsupported methods, log and fall back to normal flow
                                    if (ex.ToString().Contains("engine_getPayloadV3 is not supported") ||
                                        ex.ToString().Contains("The method 'engine_getPayloadV3' is not supported"))
                                    {
                                        _logger.Warn($"Validation flow skipped due to unsupported methods: {ex.Message}");
                                        _logger.Info("Falling back to direct forwarding of request to execution client");
                                        return await ForwardRequestToExecutionClient(request);
                                    }
                                    
                                    // For other errors, throw to be caught by the outer try/catch
                                    throw;
                                }
                            }
                        }
                        
                        // If we couldn't get the head block hash, just forward the request
                        _logger.Warn("Could not extract head block hash, forwarding request as-is");
                        return await ForwardRequestToExecutionClient(request);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Error in validation flow: {ex.Message}", ex);
                        return await ForwardRequestToExecutionClient(request);
                    }
                    finally
                    {
                        // Always resume processing, even if there was an error
                        _logger.Info("Resuming message queue processing");
                        _messageQueue.ResumeProcessing();
                    }
                }
                
                // Forward the request to EC without modification if not validating
                var response = await ForwardRequestToExecutionClient(request);
                
                // If response contains payloadId, store it for tracking
                if (response.Result is JObject resultObj && 
                    resultObj["payloadId"] != null && 
                    request.Params != null && 
                    request.Params.Count > 0 && 
                    request.Params[0] is JObject fcState &&
                    fcState["headBlockHash"] != null)
                {
                    string payloadId = resultObj["payloadId"]?.ToString() ?? string.Empty;
                    string headBlockHashStr = fcState["headBlockHash"]?.ToString() ?? string.Empty;
                    
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
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling forkChoiceUpdated: {ex.Message}");
            }
        }

        private async Task<JsonRpcResponse> HandleNewPayload(JsonRpcRequest request)
        {
            // This is a placeholder implementation - will be expanded in the NewPayloadHandler
            _logger.Debug($"Processing engine_newPayloadV3: {request}");
            
            return await ForwardRequestToExecutionClient(request);
        }

        private async Task<JsonRpcResponse> HandleGetPayload(JsonRpcRequest request)
        {
            // Log the getPayload request
            _logger.Debug($"Processing engine_getPayloadV3: {request}");
            
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
                    
                }
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error handling getPayloadV3: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling getPayloadV3: {ex.Message}");
            }
        }

        private async Task<JsonRpcResponse> ForwardRequestToExecutionClient(JsonRpcRequest request)
        {
            try
            {
                string requestJson = JsonConvert.SerializeObject(request);
                string targetHost = _httpClient.BaseAddress?.ToString() ?? "unknown";
                _logger.Debug($"Forwarding request to EL at: {targetHost}");
                _logger.Info($"PR -> EL: {requestJson}");
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
                _logger.Info($"EL -> PR: {responseBody}");
                
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

        private async Task SendErrorResponse(HttpContext context, int statusCode, string message)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            
            var errorResponse = new
            {
                jsonrpc = "2.0",
                id = (object?)null,
                error = new
                {
                    code = statusCode,
                    message = message
                }
            };
            
            await context.Response.WriteAsync(JsonConvert.SerializeObject(errorResponse));
        }
        
        // Method added specifically for testing purposes
        private async Task<JsonRpcResponse> HandleRequest(JsonRpcRequest request)
        {
            _logger.Debug($"Handle request: {request}");
            
            try
            {
                // Process the request based on method
                switch (request.Method)
                {
                    case "engine_forkChoiceUpdated":
                    case "engine_forkChoiceUpdatedV2":
                    case "engine_forkchoiceUpdatedV3":
                    case "engine_forkChoiceUpdatedV4":
                        return await HandleForkChoiceUpdated(request);
                        
                    case "engine_newPayload":
                    case "engine_newPayloadV2":
                    case "engine_newPayloadV3":
                        // Direct handling without queueing for testing
                        return await HandleNewPayload(request);
                        
                    case "engine_getPayloadV3":
                    case "engine_getPayloadV2":
                        return await HandleGetPayload(request);
                        
                    default:
                        // Forward any other methods directly to EC
                        return await ForwardRequestToExecutionClient(request);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error processing test request: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Engine API Proxy error: {ex.Message}");
            }
        }
    }
} 