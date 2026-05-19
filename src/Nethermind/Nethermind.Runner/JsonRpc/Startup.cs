// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Authentication;
using Nethermind.Core.Resettables;
using Nethermind.Facade.Eth;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.Runner.JsonRpc;

public class Startup : IStartup
{
    private JsonRpcProcessor _jsonRpcProcessor = null!;
    private JsonRpcService _jsonRpcService = null!;
    private IJsonRpcLocalStats _jsonRpcLocalStats = null!;
    private EthereumJsonSerializer _jsonSerializer = null!;
    private IJsonRpcConfig _jsonRpcConfig = null!;
    private IRpcAuthentication? _rpcAuthentication;
    private ILogger _logger = default;

    public Startup() { }

    // for tests
    internal Startup(
        JsonRpcProcessor jsonRpcProcessor,
        JsonRpcService jsonRpcService,
        IJsonRpcLocalStats jsonRpcLocalStats,
        EthereumJsonSerializer jsonSerializer,
        IJsonRpcConfig jsonRpcConfig,
        IRpcAuthentication? rpcAuthentication = null,
        ILogger logger = default
    )
    {
        _jsonRpcProcessor = jsonRpcProcessor;
        _jsonRpcService = jsonRpcService;
        _jsonRpcLocalStats = jsonRpcLocalStats;
        _jsonSerializer = jsonSerializer;
        _jsonRpcConfig = jsonRpcConfig;
        _logger = logger;
        _rpcAuthentication = rpcAuthentication;
    }

    IServiceProvider IStartup.ConfigureServices(IServiceCollection services) => Build(services);

    public void ConfigureServices(IServiceCollection services)
    {
        ServiceProvider sp = Build(services);
        IConfigProvider? configProvider = sp.GetService<IConfigProvider>() ?? throw new ApplicationException($"{nameof(IConfigProvider)} could not be resolved");
        IJsonRpcConfig jsonRpcConfig = configProvider.GetConfig<IJsonRpcConfig>();

        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = jsonRpcConfig.MaxRequestBodySize;
            options.ConfigureHttpsDefaults(co => co.SslProtocols |= SslProtocols.Tls13);
            options.ConfigureEndpointDefaults(listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
                listenOptions.DisableAltSvcHeader = true;
            });
        });
        Bootstrap.Instance.RegisterJsonRpcServices(services);

        services.AddCors(options => options.AddDefaultPolicy(builder => builder
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithOrigins(jsonRpcConfig.CorsOrigins)));

        services.AddResponseCompression(options =>
        {
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes;
            options.EnableForHttps = true;
        });
    }

    private static ServiceProvider Build(IServiceCollection services) => services.BuildServiceProvider();


    public void Configure(IApplicationBuilder app)
    {
        IServiceProvider services = app.ApplicationServices;
        Configure(
            app,
            services.GetRequiredService<IWebHostEnvironment>(),
            services.GetRequiredService<JsonRpcProcessor>(),
            services.GetRequiredService<JsonRpcService>(),
            services.GetRequiredService<IJsonRpcLocalStats>(),
            services.GetRequiredService<EthereumJsonSerializer>(),
            services.GetRequiredService<ApplicationLifetime>());
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, JsonRpcProcessor jsonRpcProcessor, JsonRpcService jsonRpcService, IJsonRpcLocalStats jsonRpcLocalStats, EthereumJsonSerializer jsonSerializer, ApplicationLifetime lifetime)
    {
        // Register source-generated type info resolvers before warmup
        EthereumJsonSerializer.AddTypeInfoResolver(JsonRpcResponseJsonContext.Default);
        EthereumJsonSerializer.AddTypeInfoResolver(FacadeJsonContext.Default);
        EthereumJsonSerializer.AddTypeInfoResolver(EthRpcJsonContext.Default);

        // Warm up System.Text.Json metadata for hot response types
        EthereumJsonSerializer.WarmupSerializer(
            new JsonRpcSuccessResponse { Id = 0 },
            new JsonRpcErrorResponse { Id = 0, Error = new Error { Code = 0, Message = string.Empty } });

        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        IConfigProvider? configProvider = app.ApplicationServices.GetService<IConfigProvider>();
        IRpcAuthentication? rpcAuthentication = app.ApplicationServices.GetService<IRpcAuthentication>();

        if (configProvider is null)
        {
            throw new ApplicationException($"{nameof(IConfigProvider)} has not been loaded properly");
        }

        ILogManager? logManager = app.ApplicationServices.GetService<ILogManager>() ?? NullLogManager.Instance;
        ILogger logger = logManager.GetClassLogger<Startup>();
        IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
        IJsonRpcConfig jsonRpcConfig = configProvider.GetConfig<IJsonRpcConfig>();
        IJsonRpcUrlCollection jsonRpcUrlCollection = app.ApplicationServices.GetRequiredService<IJsonRpcUrlCollection>();
        IHealthChecksConfig healthChecksConfig = configProvider.GetConfig<IHealthChecksConfig>();

        _jsonRpcProcessor = jsonRpcProcessor;
        _jsonRpcService = jsonRpcService;
        _jsonRpcLocalStats = jsonRpcLocalStats;
        _jsonSerializer = jsonSerializer;
        _jsonRpcConfig = jsonRpcConfig;
        _rpcAuthentication = rpcAuthentication;
        _logger = logger;

        // Engine API fast lane: authenticated engine port POST requests bypass
        // routing, CORS, compression, and WebSocket middleware
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Method != "POST" ||
                !(ctx.Request.ContentType?.Contains("application/json") ?? false) ||
                !jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl jsonRpcUrl) ||
                !jsonRpcUrl.IsAuthenticated ||
                !jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Http))
            {
                await next();
                return;
            }

            await ProcessJsonRpcRequestCoreAsync(ctx, jsonRpcUrl);
        });

        app.UseRouting();
        app.UseCors();

        // Skip response compression for localhost (low benefit, high allocation cost)
        // and for Engine API requests (latency-sensitive consensus path)
        app.UseWhen(ctx =>
            !IsLocalhost(ctx.Connection.RemoteIpAddress!) &&
            !(jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl url) && url.IsAuthenticated),
            builder => builder.UseResponseCompression());

        if (initConfig.WebSocketsEnabled)
        {
            app.UseWebSockets(new WebSocketOptions());
            app.UseWhen(ctx =>
                ctx.WebSockets.IsWebSocketRequest &&
                jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl jsonRpcUrl) &&
                jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Ws),
            builder => builder.UseWebSocketsModules());
        }

        string[] healthHostPatterns = jsonRpcUrlCollection.Values
            .Where(url => url.IsModuleEnabled(ModuleType.Health))
            .Select(url => $"*:{url.Port}")
            .ToArray();

        app.UseEndpoints(endpoints =>
        {
            if (healthChecksConfig.Enabled && healthHostPatterns.Length > 0)
            {
                try
                {
                    endpoints.MapHealthChecks(healthChecksConfig.Slug, new HealthCheckOptions()
                    {
                        Predicate = _ => true,
                        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
                    }).RequireHost(healthHostPatterns);
                    if (healthChecksConfig.UIEnabled)
                    {
                        endpoints.MapHealthChecksUI(setup => setup.AddCustomStylesheet(Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, "nethermind.css")))
                            .RequireHost(healthHostPatterns);
                    }
                    endpoints.MapDataFeeds(lifetime).RequireHost(healthHostPatterns);
                }
                catch (Exception e)
                {
                    if (logger.IsError) logger.Error("Unable to initialize health checks. Check if you have Nethermind.HealthChecks.dll in your plugins folder.", e);
                }
            }
        });

        app.MapWhen(
            ctx => ctx.Request.ContentType?.Contains("application/json") ?? false,
            builder => builder.Run(async ctx =>
        {
            string method = ctx.Request.Method;
            if (method is not "POST" and not "GET")
            {
                ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            if (!jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl jsonRpcUrl) || !jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Http))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (method == "GET" && ctx.Request.Headers.Accept.Count > 0 &&
                !ctx.Request.Headers.Accept[0]!.Contains("text/html", StringComparison.Ordinal))
            {
                await ctx.Response.WriteAsync("Nethermind JSON RPC");
            }
            else if (ctx.Request.ContentType?.Contains("application/json") == false)
            {
                await PushErrorResponseAsync(ctx, StatusCodes.Status415UnsupportedMediaType, ErrorCodes.InvalidRequest, "Missing 'application/json' Content-Type header");
            }
            else
            {
                await ProcessJsonRpcRequestCoreAsync(ctx, jsonRpcUrl);
            }
        }));

        if (healthChecksConfig.Enabled && healthHostPatterns.Length > 0)
        {
            ManifestEmbeddedFileProvider fileProvider = new(typeof(Startup).Assembly, "wwwroot");

            app.UseWhen(
                ctx => jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl url) && url.IsModuleEnabled(ModuleType.Health),
                builder =>
                {
                    builder.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
                    builder.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
                });
        }
    }

    /// <summary>
    /// Check for IPv4 localhost (127.0.0.1) and IPv6 localhost (::1)
    /// </summary>
    /// <param name="remoteIp">Request source</param>
    private static bool IsLocalhost(IPAddress remoteIp)
        => IPAddress.IsLoopback(remoteIp) || remoteIp.Equals(IPAddress.IPv6Loopback);

    private async Task PushErrorResponseAsync(HttpContext ctx, int statusCode, int errorCode, string message)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = statusCode;
        JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(errorCode, message);
        await _jsonSerializer.SerializeAsync(ctx.Response.BodyWriter, response);
        await ctx.Response.CompleteAsync();
    }

    internal async Task ProcessJsonRpcRequestCoreAsync(HttpContext ctx, JsonRpcUrl jsonRpcUrl)
    {
        if (_jsonRpcProcessor.ProcessExit.IsCancellationRequested)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (jsonRpcUrl.IsAuthenticated && !await _rpcAuthentication!.Authenticate(ctx.Request.Headers.Authorization))
        {
            await PushErrorResponseAsync(ctx, StatusCodes.Status401Unauthorized, ErrorCodes.InvalidRequest, "Authentication error");
            return;
        }

        if (jsonRpcUrl.MaxRequestBodySize is not null)
        {
            IHttpMaxRequestBodySizeFeature? maxRequestBodySizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxRequestBodySizeFeature is not null)
            {
                maxRequestBodySizeFeature.MaxRequestBodySize = jsonRpcUrl.MaxRequestBodySize;
            }
        }

        long startTime = Stopwatch.GetTimestamp();
        long? contentLength = ctx.Request.ContentLength;
        long? effectiveMaxRequestBodySize = jsonRpcUrl.MaxRequestBodySize ?? _jsonRpcConfig.MaxRequestBodySize;
        CollectedHttpBody collectedBody = new();
        HttpJsonRpcResponseSink? responseSink = null;
        try
        {
            PipeReader request = await CollectHttpRequestBodyAsync(ctx, contentLength, effectiveMaxRequestBodySize, collectedBody, ctx.RequestAborted);
            using JsonRpcContext jsonRpcContext = JsonRpcContext.Http(jsonRpcUrl);
            responseSink = new HttpJsonRpcResponseSink(ctx, jsonRpcUrl, _jsonRpcConfig, _jsonRpcLocalStats, EthereumJsonSerializer.JsonOptions, _logger, startTime);

            await _jsonRpcProcessor.ProcessAsync(
                request,
                jsonRpcContext,
                responseSink,
                new JsonRpcProcessingOptions(JsonRpcInputMode.SingleDocument),
                ctx.RequestAborted);
        }
        catch (Exception e) when (e is OperationCanceledException || e.InnerException is OperationCanceledException)
        {
            JsonRpcErrorResponse error = _jsonRpcService.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
            responseSink ??= new HttpJsonRpcResponseSink(ctx, jsonRpcUrl, _jsonRpcConfig, _jsonRpcLocalStats, EthereumJsonSerializer.JsonOptions, _logger, startTime);
            await responseSink.WriteSingleAsync(error, RpcReport.Error, ctx.RequestAborted);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
        {
            responseSink = null;
            if (_logger.IsDebug) LogBadRequest(_logger, e);
            ctx.Response.ContentType = "application/json";
            ctx.Response.StatusCode = e.StatusCode;
            JsonRpcErrorResponse errResp = _jsonRpcService.GetErrorResponse(
                e.StatusCode == StatusCodes.Status413PayloadTooLarge ? ErrorCodes.LimitExceeded : ErrorCodes.InvalidRequest,
                e.Message);
            await _jsonSerializer.SerializeAsync(ctx.Response.BodyWriter, errResp);
            await ctx.Response.CompleteAsync();
        }
        finally
        {
            if (responseSink is not null)
            {
                await responseSink.CompleteAsync(ctx.RequestAborted);
            }

            Interlocked.Add(ref Metrics.JsonRpcBytesReceivedHttp, collectedBody.BytesRead);
        }
    }

    private static async ValueTask<PipeReader> CollectHttpRequestBodyAsync(
        HttpContext context,
        long? contentLength,
        long? maxRequestBodySize,
        CollectedHttpBody collectedBody,
        CancellationToken cancellationToken)
    {
        Stream stream = contentLength is > 0
            ? RecyclableStream.GetStream("http-request", contentLength.Value)
            : RecyclableStream.GetStream("http-request");
        bool success = false;

        try
        {
            PipeReader bodyReader = context.Request.BodyReader;
            while (true)
            {
                ReadResult readResult = await bodyReader.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = readResult.Buffer;

                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    collectedBody.BytesRead += segment.Length;
                    if (maxRequestBodySize is not null && collectedBody.BytesRead > maxRequestBodySize)
                    {
                        throw new Microsoft.AspNetCore.Http.BadHttpRequestException(
                            $"Request body too large. The max request body size is {maxRequestBodySize} bytes.",
                            StatusCodes.Status413PayloadTooLarge);
                    }

                    await stream.WriteAsync(segment, cancellationToken);
                }

                bodyReader.AdvanceTo(buffer.End);

                if (readResult.IsCompleted || readResult.IsCanceled)
                {
                    break;
                }
            }

            await context.Request.BodyReader.CompleteAsync();
            stream.Seek(0, SeekOrigin.Begin);
            success = true;
            return PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: false));
        }
        finally
        {
            if (!success)
            {
                await context.Request.BodyReader.CompleteAsync();
                await stream.DisposeAsync();
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LogBadRequest(ILogger logger, Exception e) =>
        logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");

    private sealed class CollectedHttpBody
    {
        public long BytesRead { get; set; }
    }
}
