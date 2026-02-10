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
            services.GetRequiredService<IJsonRpcProcessor>(),
            services.GetRequiredService<IJsonRpcService>(),
            services.GetRequiredService<IJsonRpcLocalStats>(),
            services.GetRequiredService<IJsonSerializer>(),
            services.GetRequiredService<ApplicationLifetime>());
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IJsonRpcProcessor jsonRpcProcessor, IJsonRpcService jsonRpcService, IJsonRpcLocalStats jsonRpcLocalStats, IJsonSerializer jsonSerializer, ApplicationLifetime lifetime)
    {
        // Capture concrete types for devirtualization in hot-path closures
        JsonRpcProcessor concreteProcessor = (JsonRpcProcessor)jsonRpcProcessor;
        JsonRpcService concreteService = (JsonRpcService)jsonRpcService;
        EthereumJsonSerializer concreteSerializer = (EthereumJsonSerializer)jsonSerializer;

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

            if (concreteProcessor.ProcessExit.IsCancellationRequested)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            if (!await rpcAuthentication!.Authenticate(ctx.Request.Headers.Authorization))
            {
                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                JsonRpcErrorResponse authError = concreteService.GetErrorResponse(ErrorCodes.InvalidRequest, "Authentication error");
                await concreteSerializer.SerializeAsync(ctx.Response.BodyWriter, authError);
                await ctx.Response.CompleteAsync();
                return;
            }

            if (jsonRpcUrl.MaxRequestBodySize is not null)
                ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = jsonRpcUrl.MaxRequestBodySize;

            long startTime = Stopwatch.GetTimestamp();
            // Engine API: skip CountingPipeReader, use Content-Length for metrics
            PipeReader request = ctx.Request.BodyReader;
            try
            {
                using JsonRpcContext jsonRpcContext = JsonRpcContext.Http(jsonRpcUrl);
                await foreach (JsonRpcResult result in concreteProcessor.ProcessAsync(request, jsonRpcContext))
                {
                    using (result)
                    {
                        // Engine API: always write directly to response body (no buffering)
                        CountingPipeWriter resultWriter = new(ctx.Response.BodyWriter);
                        try
                        {
                            ctx.Response.ContentType = "application/json";
                            ctx.Response.StatusCode = GetStatusCode(result);
                            await ctx.Response.StartAsync();

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
                                            await concreteSerializer.SerializeAsync(resultWriter, entry.Response);
                                            _ = jsonRpcLocalStats.ReportCall(entry.Report);
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
                        }
                        catch (Exception e) when (e.InnerException is OperationCanceledException)
                        {
                            await SerializeTimeoutException(resultWriter);
                        }
                        catch (OperationCanceledException)
                        {
                            await SerializeTimeoutException(resultWriter);
                        }
                        finally
                        {
                            await ctx.Response.CompleteAsync();
                        }

                        long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds;
                        _ = jsonRpcLocalStats.ReportCall(result.IsCollection
                            ? new RpcReport("# collection serialization #", handlingTimeMicroseconds, true)
                            : result.Report.Value, handlingTimeMicroseconds, resultWriter.WrittenCount);
                        Interlocked.Add(ref Metrics.JsonRpcBytesSentHttp, resultWriter.WrittenCount);
                        break;
                    }
                }

                Task SerializeTimeoutException(CountingPipeWriter resultStream)
                {
                    JsonRpcErrorResponse error = concreteService.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                    return concreteSerializer.SerializeAsync(resultStream, error);
                }
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
            {
                if (logger.IsDebug) LogBadRequest(logger, e);
                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = e.StatusCode;
                JsonRpcErrorResponse errResp = concreteService.GetErrorResponse(
                    e.StatusCode == StatusCodes.Status413PayloadTooLarge ? ErrorCodes.LimitExceeded : ErrorCodes.InvalidRequest,
                    e.Message);
                await concreteSerializer.SerializeAsync(ctx.Response.BodyWriter, errResp);
                await ctx.Response.CompleteAsync();
            }
            finally
            {
                Interlocked.Add(ref Metrics.JsonRpcBytesReceivedHttp, ctx.Request.ContentLength ?? 0);
            }
        });

        app.UseRouting();
        app.UseCors();

        // If request is local, don't use response compression,
        // as it allocates a lot, but doesn't improve much for loopback
        app.UseWhen(ctx =>
            !IsLocalhost(ctx.Connection.RemoteIpAddress!),
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
            (ctx) => ctx.Request.ContentType?.Contains("application/json") ?? false,
            builder => builder.Run(async ctx =>
        {
            var method = ctx.Request.Method;
            if (method is not "POST" and not "GET")
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (concreteProcessor.ProcessExit.IsCancellationRequested)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                return;
            }

            if (!jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl jsonRpcUrl) || !jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Http))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (jsonRpcUrl.IsAuthenticated)
            {
                if (!await rpcAuthentication!.Authenticate(ctx.Request.Headers.Authorization))
                {
                    await PushErrorResponse(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidRequest, "Authentication error");
                    return;
                }
            }

            if (method == "GET" && ctx.Request.Headers.Accept.Count > 0 &&
                !ctx.Request.Headers.Accept[0]!.Contains("text/html", StringComparison.Ordinal))
            {
                await ctx.Response.WriteAsync("Nethermind JSON RPC");
            }
            else if (ctx.Request.ContentType?.Contains("application/json") == false)
            {
                await PushErrorResponse(StatusCodes.Status415UnsupportedMediaType, ErrorCodes.InvalidRequest, "Missing 'application/json' Content-Type header");
            }
            else
            {
                if (jsonRpcUrl.MaxRequestBodySize is not null)
                    ctx.Features.Get<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = jsonRpcUrl.MaxRequestBodySize;

                long startTime = Stopwatch.GetTimestamp();
                // Skip CountingPipeReader when Content-Length is known
                long? knownContentLength = ctx.Request.ContentLength;
                CountingPipeReader countingReader = knownContentLength > 0 ? null : new(ctx.Request.BodyReader);
                PipeReader request = countingReader ?? ctx.Request.BodyReader;
                try
                {
                    using JsonRpcContext jsonRpcContext = JsonRpcContext.Http(jsonRpcUrl);
                    await foreach (JsonRpcResult result in concreteProcessor.ProcessAsync(request, jsonRpcContext))
                    {
                        using (result)
                        {
                            // Authenticated (Engine API) single responses bypass buffering to avoid double-copy
                            bool bufferResponse = jsonRpcConfig.BufferResponses && !(jsonRpcUrl.IsAuthenticated && !result.IsCollection);
                            await using Stream stream = bufferResponse ? RecyclableStream.GetStream("http") : null;
                            CountingWriter resultWriter = stream is not null ? new CountingStreamPipeWriter(stream) : new CountingPipeWriter(ctx.Response.BodyWriter);
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
                                                if (!first)
                                                {
                                                    resultWriter.Write(_jsonComma);
                                                }

                                                first = false;
                                                await concreteSerializer.SerializeAsync(resultWriter, entry.Response);
                                                _ = jsonRpcLocalStats.ReportCall(entry.Report);

                                                // We reached the limit and don't want to respond to more request in the batch
                                                if (!jsonRpcContext.IsAuthenticated && resultWriter.WrittenCount > jsonRpcConfig.MaxBatchResponseBodySize)
                                                {
                                                    if (logger.IsWarn) logger.Warn($"The max batch response body size exceeded. The current response size {resultWriter.WrittenCount}, and the config setting is JsonRpc.{nameof(jsonRpcConfig.MaxBatchResponseBodySize)} = {jsonRpcConfig.MaxBatchResponseBodySize}");
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
                            catch (Exception e) when (e.InnerException is OperationCanceledException)
                            {
                                await SerializeTimeoutException(resultWriter);
                            }
                            catch (OperationCanceledException)
                            {
                                await SerializeTimeoutException(resultWriter);
                            }
                            finally
                            {
                                await ctx.Response.CompleteAsync();
                            }

                            long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds;
                            _ = jsonRpcLocalStats.ReportCall(result.IsCollection
                                ? new RpcReport("# collection serialization #", handlingTimeMicroseconds, true)
                                : result.Report.Value, handlingTimeMicroseconds, resultWriter.WrittenCount);

                            Interlocked.Add(ref Metrics.JsonRpcBytesSentHttp, resultWriter.WrittenCount);

                            // There should be only one response because we don't expect multiple JSON tokens in the request
                            break;
                        }
                    }
                }
                catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
                {
                    if (logger.IsDebug) LogBadRequest(logger, e);
                    await PushErrorResponse(e.StatusCode, e.StatusCode == StatusCodes.Status413PayloadTooLarge
                                            ? ErrorCodes.LimitExceeded
                                            : ErrorCodes.InvalidRequest,
                                            e.Message);
                }
                finally
                {
                    Interlocked.Add(ref Metrics.JsonRpcBytesReceivedHttp, knownContentLength ?? countingReader!.Length);
                }
            }
            Task SerializeTimeoutException(CountingWriter resultStream)
            {
                JsonRpcErrorResponse? error = concreteService.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                return concreteSerializer.SerializeAsync(resultStream, error);
            }
            async Task PushErrorResponse(int statusCode, int errorCode, string message)
            {
                JsonRpcErrorResponse? response = concreteService.GetErrorResponse(errorCode, message);
                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = statusCode;
                await concreteSerializer.SerializeAsync(ctx.Response.BodyWriter, response);
                await ctx.Response.CompleteAsync();
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
