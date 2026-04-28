// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Handlers;
using Nethermind.EngineApiProxy.Models;
using Nethermind.EngineApiProxy.Services;
using Nethermind.EngineApiProxy.Utilities;
using Nethermind.Logging;

namespace Nethermind.EngineApiProxy;

public class ProxyServer
{
    private readonly ProxyConfig _config;
    private readonly ILogger _logger;
    private readonly IHost _webHost;
    private readonly HttpClient _httpClient;
    private readonly HttpClient? _consensusClient;

    // Components for state management and message queueing
    private readonly MessageQueue _messageQueue;
    private readonly PayloadTracker _payloadTracker;

    // Handlers for different request types
    private readonly ForkChoiceUpdatedHandler _forkChoiceUpdatedHandler;
    private readonly NewPayloadHandler _newPayloadHandler;
    private readonly GetPayloadHandler _getPayloadHandler;
    private readonly DefaultRequestHandler _defaultRequestHandler;

    // Utility services
    private readonly RequestForwarder _requestForwarder;
    private Task? _messageProcessingTask;

    public ProxyServer(ProxyConfig config, ILogManager logManager)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logManager?.GetClassLogger<ProxyServer>() ?? throw new ArgumentNullException(nameof(logManager));

        _logger.Info("Setting up logging...");

        // Validate configuration
        if (string.IsNullOrWhiteSpace(_config.ExecutionClientEndpoint))
        {
            throw new ArgumentException("Execution client endpoint must be provided", nameof(config));
        }

        _httpClient = new HttpClient(CreateSocketsHttpHandler())
        {
            BaseAddress = new Uri(_config.ExecutionClientEndpoint),
            Timeout = TimeSpan.FromSeconds(_config.RequestTimeoutSeconds)
        };

        // Initialize consensus client HttpClient if endpoint is configured
        if (!string.IsNullOrWhiteSpace(_config.ConsensusClientEndpoint))
        {
            _logger.Info($"Configuring consensus client with endpoint: {_config.ConsensusClientEndpoint}");

            _consensusClient = new HttpClient(CreateSocketsHttpHandler())
            {
                BaseAddress = new Uri(_config.ConsensusClientEndpoint),
                Timeout = TimeSpan.FromSeconds(_config.RequestTimeoutSeconds)
            };
        }
        else
        {
            _logger.Info("No consensus client endpoint configured. CL features will be disabled.");
            _consensusClient = null;
        }

        // Initialize core components
        _messageQueue = new MessageQueue(logManager);
        _payloadTracker = new PayloadTracker(logManager);

        // Initialize utilities
        _requestForwarder = new RequestForwarder(_httpClient, config, logManager);

        // Initialize specialized components
        BlockDataFetcher blockDataFetcher = new(_httpClient, logManager, _consensusClient);
        // Set CL endpoint on block data fetcher for reference
        if (_consensusClient is not null)
        {
            blockDataFetcher.ConsensusClientEndpoint = _config.ConsensusClientEndpoint;
        }

        PayloadAttributesGenerator payloadAttributesGenerator = new(config, logManager);
        RequestOrchestrator requestOrchestrator = new(
            _httpClient,
            blockDataFetcher,
            payloadAttributesGenerator,
            _messageQueue,
            _payloadTracker,
            config,
            logManager);

        // Initialize request handlers
        _forkChoiceUpdatedHandler = new ForkChoiceUpdatedHandler(
            config,
            _requestForwarder,
            _messageQueue,
            _payloadTracker,
            requestOrchestrator,
            logManager);

        _newPayloadHandler = new NewPayloadHandler(
            config,
            _requestForwarder,
            _messageQueue,
            _payloadTracker,
            requestOrchestrator,
            logManager);

        _getPayloadHandler = new GetPayloadHandler(
            config,
            _requestForwarder,
            _payloadTracker,
            logManager);

        _defaultRequestHandler = new DefaultRequestHandler(
            _requestForwarder,
            logManager);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [$"--urls=http://*:{_config.ListenPort}"]
        });
        builder.Services.AddSingleton(this);
        builder.Services.AddRouting();

        WebApplication app = builder.Build();
        app.UseRouting();
        app.MapPost("/", HandleCLJsonRpcRequest);

        _webHost = app;

        _logger.Info($"Proxy configured with {_config}");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _webHost.StartAsync(cancellationToken);

        // Start background tasks for message processing
        _messageProcessingTask = ProcessMessageQueueAsync(cancellationToken);

        _logger.Info($"Engine API Proxy started on port {_config.ListenPort}");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _webHost.StopAsync(cancellationToken);

        // Web host has stopped accepting new requests; finalise the queue so the consumer can
        // drain remaining work and any pending EnqueueMessage awaiters are cancelled rather than
        // left hanging until ASP.NET drops the connection.
        _messageQueue.Complete();

        // Wait for background processing to finish
        if (_messageProcessingTask is not null)
        {
            try
            {
                await _messageProcessingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                _logger.Error($"Background message processing faulted: {ex.Message}", ex);
            }
        }

        // Dispose of the handlers that require it
        if (_forkChoiceUpdatedHandler is IDisposable disposableHandler)
        {
            disposableHandler.Dispose();
            _logger.Debug("Disposed ForkChoiceUpdatedHandler");
        }

        // Dispose HTTP clients and web host
        _httpClient.Dispose();
        _consensusClient?.Dispose();
        if (_webHost is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }

        _logger.Info("Engine API Proxy stopped");
    }

    private async Task ProcessMessageQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            QueuedMessage? message;
            try
            {
                message = await _messageQueue.DequeueNextMessageAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (message is null)
            {
                break;
            }

            try
            {
                JsonRpcResponse response = await HandleRequest(message.Request);
                message.CompletionTask.TrySetResult(response);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error processing message {message.Request.Method}: {ex.Message}", ex);
                message.CompletionTask.TrySetException(ex);
            }
        }
    }

    private async Task HandleCLJsonRpcRequest(HttpContext context)
    {
        string requestBody;
        using (StreamReader reader = new(context.Request.Body, Encoding.UTF8))
        {
            requestBody = await reader.ReadToEndAsync();
        }

        string sourceIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string sourceHost = context.Request.Headers.TryGetValue("Host", out StringValues value) ? value.ToString() : "unknown";
        string method;
        try
        {
            JsonObject? requestObj = JsonNode.Parse(requestBody) as JsonObject;
            method = requestObj?["method"]?.ToString() ?? "unknown";
        }
        catch
        {
            method = "unknown";
        }
        _logger.Info($"CL -> PR|{method}|(Source IP: {sourceIp}, Headers Host: {sourceHost}): {requestBody}");

        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<JsonRpcRequest>(requestBody);
            if (request is null)
            {
                await SendErrorResponse(context, 400, "Invalid JSON-RPC request");
                return;
            }

            // Store the original request headers for per-request forwarding (Authorization included).
            request.OriginalHeaders = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());

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
            // Process the request or queue it as needed
            if (request.Method.StartsWith("engine_newPayload") && _config.ValidationMode == ValidationMode.NewPayload)
            {
                // Queue the newPayload message and wait for the response
                Task<JsonRpcResponse> responseTask = _messageQueue.EnqueueMessage(request);
                response = await responseTask;
                _logger.Debug($"Received response from message queue for {request.Method}: {JsonSerializer.Serialize(response)}");
            }
            // In Lighthouse mode, we want to handle FCU requests specially but still queue them
            else if (request.Method.StartsWith("engine_forkchoiceUpdated") &&
                    (_config.ValidationMode == ValidationMode.ForkChoiceUpdated ||
                     _config.ValidationMode == ValidationMode.Merged ||
                     _config.ValidationMode == ValidationMode.Lighthouse))
            {
                // Queue the forkChoiceUpdated message and wait for the response
                Task<JsonRpcResponse> responseTask = _messageQueue.EnqueueMessage(request);
                response = await responseTask;
                _logger.Debug($"Received response from message queue for {request.Method}: {JsonSerializer.Serialize(response)}");
            }
            else
            {
                // Handle the request directly
                response = await HandleRequest(request);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing request: {ex.Message}", ex);
            response = JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Engine API Proxy error: {ex.Message}");
        }

        context.Response.ContentType = "application/json";
        string destinationIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string destinationHost = context.Request.Headers.TryGetValue("Host", out StringValues val) ? val.ToString() : "unknown";
        _logger.Info($"PR -> CL|{request.Method}|(Destination IP: {destinationIp}, Headers Host: {destinationHost}): {JsonSerializer.Serialize(response)}");
        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }

    private async Task<JsonRpcResponse> HandleRequest(JsonRpcRequest request)
    {
        _logger.Debug($"Handle request: {request}");

        try
        {
            // Route request to appropriate handler based on method
            return request.Method switch
            {
                // Fork choice updated methods
                var method when method.StartsWith("engine_forkchoiceUpdated") =>
                    await _forkChoiceUpdatedHandler.HandleRequest(request),

                // New payload methods
                var method when method.StartsWith("engine_newPayload") =>
                    await _newPayloadHandler.HandleRequest(request),

                // Get payload methods
                var method when method.StartsWith("engine_getPayload") =>
                    await _getPayloadHandler.HandleRequest(request),

                // Default case - forward any other methods
                _ => await _defaultRequestHandler.HandleRequest(request)
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Error routing request: {ex.Message}", ex);
            return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error: Routing request: {ex.Message}");
        }
    }

    private static SocketsHttpHandler CreateSocketsHttpHandler() => new()
    {
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
        KeepAlivePingTimeout = TimeSpan.FromSeconds(180),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        EnableMultipleHttp2Connections = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    };

    private static async Task SendErrorResponse(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        JsonObject errorResponse = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject
            {
                ["code"] = statusCode,
                ["message"] = "Proxy error: " + message
            }
        };

        await context.Response.WriteAsync(errorResponse.ToJsonString());
    }
}
