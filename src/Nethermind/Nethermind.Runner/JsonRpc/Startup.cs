// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
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

    private static ReadOnlySpan<byte> _jsonOpeningBracket => [(byte)'['];
    private static ReadOnlySpan<byte> _jsonComma => [(byte)','];
    private static ReadOnlySpan<byte> _jsonClosingBracket => [(byte)']'];

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
        ILogger logger = logManager.GetClassLogger();
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

        app.UseEndpoints(endpoints =>
        {
            if (healthChecksConfig.Enabled)
            {
                try
                {
                    endpoints.MapHealthChecks(healthChecksConfig.Slug, new HealthCheckOptions()
                    {
                        Predicate = _ => true,
                        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
                    });
                    if (healthChecksConfig.UIEnabled)
                    {
                        endpoints.MapHealthChecksUI(setup => setup.AddCustomStylesheet(Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, "nethermind.css")));
                    }
                }
                catch (Exception e)
                {
                    if (logger.IsError) logger.Error("Unable to initialize health checks. Check if you have Nethermind.HealthChecks.dll in your plugins folder.", e);
                }

                endpoints.MapDataFeeds(lifetime);
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

        if (healthChecksConfig.Enabled)
        {
            var fileProvider = new ManifestEmbeddedFileProvider(typeof(Startup).Assembly, "wwwroot");

            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
        }
    }

    /// <summary>
    /// Check for IPv4 localhost (127.0.0.1) and IPv6 localhost (::1)
    /// </summary>
    /// <param name="remoteIp">Request source</param>
    private static bool IsLocalhost(IPAddress remoteIp)
        => IPAddress.IsLoopback(remoteIp) || remoteIp.Equals(IPAddress.IPv6Loopback);

    private static int GetStatusCode(in JsonRpcResult result)
    {
        if (result.IsCollection)
        {
            return StatusCodes.Status200OK;
        }
        else
        {
            return IsResourceUnavailableError(result.Response)
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK;
        }
    }

    private static bool IsResourceUnavailableError(JsonRpcResponse? response)
    {
        return response is JsonRpcErrorResponse { Error.Code: ErrorCodes.ModuleTimeout }
                    or JsonRpcErrorResponse { Error.Code: ErrorCodes.LimitExceeded };
    }

    private async Task PushErrorResponseAsync(HttpContext ctx, int statusCode, int errorCode, string message)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = statusCode;
        JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(errorCode, message);
        await _jsonSerializer.SerializeAsync(ctx.Response.BodyWriter, response);
        await ctx.Response.CompleteAsync();
    }

    private async Task ProcessJsonRpcRequestCoreAsync(HttpContext ctx, JsonRpcUrl jsonRpcUrl)
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
            ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = jsonRpcUrl.MaxRequestBodySize;

        long startTime = Stopwatch.GetTimestamp();
        // Skip CountingPipeReader when Content-Length is known
        long? knownContentLength = ctx.Request.ContentLength;
        CountingPipeReader? countingReader = knownContentLength > 0 ? null : new(ctx.Request.BodyReader);
        PipeReader request = countingReader ?? ctx.Request.BodyReader;
        try
        {
            using JsonRpcContext jsonRpcContext = JsonRpcContext.Http(jsonRpcUrl);
            await foreach (JsonRpcResult result in _jsonRpcProcessor.ProcessAsync(request, jsonRpcContext))
            {
                using (result)
                {
                    // Authenticated single responses bypass buffering to avoid double-copy
                    bool bufferResponse = _jsonRpcConfig.BufferResponses && !(jsonRpcUrl.IsAuthenticated && !result.IsCollection);
                    await using Stream stream = bufferResponse ? RecyclableStream.GetStream("http") : null;
                    CountingWriter resultWriter = stream is not null ? new CountingStreamPipeWriter(stream) : new CountingStreamPipeWriter(ctx.Response.Body);
                    try
                    {
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.StatusCode = GetStatusCode(result);

                        // Flush headers before body for unbuffered responses
                        if (stream is null)
                        {
                            await ctx.Response.StartAsync();
                        }

                        if (result.IsCollection)
                        {
                            resultWriter.Write(_jsonOpeningBracket);
                            bool first = true;
                            JsonRpcBatchResultAsyncEnumerator enumerator = result.BatchedResponses.GetAsyncEnumerator(CancellationToken.None);
                            try
                            {
                                while (await enumerator.MoveNextAsync())
                                {
                                    JsonRpcResult.Entry entry = enumerator.Current;
                                    using (entry)
                                    {
                                        if (!first) resultWriter.Write(_jsonComma);
                                        first = false;
                                        await _jsonSerializer.SerializeAsync(resultWriter, entry.Response);
                                        _ = _jsonRpcLocalStats.ReportCall(entry.Report);

                                        // Stop batch if non-authenticated response exceeds configured size limit
                                        if (!jsonRpcContext.IsAuthenticated && resultWriter.WrittenCount > _jsonRpcConfig.MaxBatchResponseBodySize)
                                        {
                                            if (_logger.IsWarn) _logger.Warn($"The max batch response body size exceeded. The current response size {resultWriter.WrittenCount}, and the config setting is JsonRpc.{nameof(_jsonRpcConfig.MaxBatchResponseBodySize)} = {_jsonRpcConfig.MaxBatchResponseBodySize}");
                                            enumerator.IsStopped = true;
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                await enumerator.DisposeAsync();
                            }
                            resultWriter.Write(_jsonClosingBracket);
                        }
                        else if (result.Response is JsonRpcSuccessResponse { Result: IStreamableResult streamable })
                        {
                            await WriteStreamableResponseAsync(resultWriter, result.Response, streamable, ctx.RequestAborted);
                        }
                        else
                        {
                            WriteJsonRpcResponse(resultWriter, result.Response);
                        }
                        await resultWriter.CompleteAsync();
                        if (stream is not null)
                        {
                            ctx.Response.ContentLength = resultWriter.WrittenCount;
                            stream.Seek(0, SeekOrigin.Begin);
                            await stream.CopyToAsync(ctx.Response.Body);
                        }
                    }
                    catch (Exception e) when (e is OperationCanceledException || e.InnerException is OperationCanceledException)
                    {
                        JsonRpcErrorResponse error = _jsonRpcService.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                        await _jsonSerializer.SerializeAsync(resultWriter, error);
                    }
                    finally
                    {
                        await ctx.Response.CompleteAsync();
                    }

                    long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds;
                    _ = _jsonRpcLocalStats.ReportCall(result.IsCollection
                        ? new RpcReport("# collection serialization #", handlingTimeMicroseconds, true)
                        : result.Report.Value, handlingTimeMicroseconds, resultWriter.WrittenCount);
                    Interlocked.Add(ref Metrics.JsonRpcBytesSentHttp, resultWriter.WrittenCount);
                    break;
                }
            }
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
        {
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
            Interlocked.Add(ref Metrics.JsonRpcBytesReceivedHttp, knownContentLength ?? countingReader?.Length ?? 0);
        }
    }

    /// <summary>
    /// Writes a JSON-RPC response with typed serialization for the result/error payload,
    /// avoiding polymorphic dispatch through the JsonRpcResponse base class hierarchy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteJsonRpcResponse(IBufferWriter<byte> writer, JsonRpcResponse response)
    {
        using var jsonWriter = new Utf8JsonWriter(writer, new JsonWriterOptions { SkipValidation = true });

        jsonWriter.WriteStartObject();
        jsonWriter.WriteString("jsonrpc"u8, "2.0"u8);

        if (response is JsonRpcSuccessResponse successResponse)
        {
            jsonWriter.WritePropertyName("result"u8);
            object? result = successResponse.Result;
            if (result is not null)
            {
                JsonSerializer.Serialize(jsonWriter, result, result.GetType(), EthereumJsonSerializer.JsonOptions);
            }
            else
            {
                jsonWriter.WriteNullValue();
            }
        }
        else if (response is JsonRpcErrorResponse errorResponse)
        {
            jsonWriter.WritePropertyName("error"u8);
            if (errorResponse.Error is not null)
            {
                JsonSerializer.Serialize(jsonWriter, errorResponse.Error, EthereumJsonSerializer.JsonOptions);
            }
            else
            {
                jsonWriter.WriteNullValue();
            }
        }

        jsonWriter.WritePropertyName("id"u8);
        WriteId(jsonWriter, response.Id);

        jsonWriter.WriteEndObject();
    }

    private static void WriteId(Utf8JsonWriter writer, object? id)
    {
        switch (id)
        {
            case int intId:
                writer.WriteNumberValue(intId);
                break;
            case long longId:
                writer.WriteNumberValue(longId);
                break;
            case string strId:
                writer.WriteStringValue(strId);
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                WriteOther(writer, id);
                break;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WriteOther(Utf8JsonWriter writer, object? id)
        {
            JsonSerializer.Serialize(writer, id, id.GetType(), EthereumJsonSerializer.JsonOptions);
        }
    }

    private static async ValueTask WriteStreamableResponseAsync(
        CountingWriter writer, JsonRpcResponse response,
        IStreamableResult streamable, CancellationToken ct)
    {
        writer.Write("{\"jsonrpc\":\"2.0\",\"result\":"u8);
        await streamable.WriteToAsync(writer, ct);
        writer.Write(",\"id\":"u8);
        WriteIdRaw(writer, response.Id);
        writer.Write("}"u8);
    }

    private static void WriteIdRaw(PipeWriter writer, object? id)
    {
        switch (id)
        {
            case int intId:
                {
                    Span<byte> buf = writer.GetSpan(11);
                    intId.TryFormat(buf, out int written);
                    writer.Advance(written);
                    break;
                }
            case long longId:
                {
                    Span<byte> buf = writer.GetSpan(20);
                    longId.TryFormat(buf, out int written);
                    writer.Advance(written);
                    break;
                }
            default:
                WriteOther(writer, id);
                break;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WriteOther(PipeWriter writer, object? id)
        {
            switch (id)
            {
                case string strId:
                    {
                        // JSON-RPC IDs are simple values (typically numeric); no escaping needed
                        Span<byte> buf = writer.GetSpan(strId.Length * 3 + 2);
                        buf[0] = (byte)'"';
                        int len = Encoding.UTF8.GetBytes(strId, buf[1..]);
                        buf[len + 1] = (byte)'"';
                        writer.Advance(len + 2);
                        break;
                    }
                default:
                    writer.Write("null"u8);
                    break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LogBadRequest(ILogger logger, Exception e) =>
        logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");

    private sealed class CountingPipeReader(PipeReader stream) : PipeReader
    {
        private ReadOnlySequence<byte> _currentSequence;

        public long Length { get; private set; }

        public override void AdvanceTo(SequencePosition consumed)
        {
            Length += _currentSequence.GetOffset(consumed);
            stream.AdvanceTo(consumed);
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            Length += _currentSequence.GetOffset(consumed);
            stream.AdvanceTo(consumed, examined);
        }

        public override void CancelPendingRead()
        {
            stream.CancelPendingRead();
        }

        public override void Complete(Exception? exception = null)
        {
            Length += _currentSequence.Length;
            stream.Complete(exception);
        }

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadResult result = await stream.ReadAsync(cancellationToken);
            _currentSequence = result.Buffer;
            return result;
        }

        public override bool TryRead(out ReadResult result)
        {
            bool didRead = stream.TryRead(out result);
            if (didRead)
            {
                _currentSequence = result.Buffer;
            }

            return didRead;
        }
    }
}
