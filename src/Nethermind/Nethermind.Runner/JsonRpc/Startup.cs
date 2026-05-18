// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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

        // Fast lane: JSON-RPC HTTP POSTs from trusted local sources dispatch
        // directly to the handler, skipping the ASP.NET middleware chain.
        // Public-IP sources fall through to the full middleware so direct
        // remote clients keep CORS and response compression. Authentication
        // is enforced inside the handler regardless of which path is taken.
        TrustedCidr[] additionalTrustedNetworks = ParseTrustedNetworks(jsonRpcConfig.AdditionalTrustedNetworks, logger);
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Method != "POST" ||
                !(ctx.Request.ContentType?.Contains("application/json") ?? false) ||
                !jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl jsonRpcUrl) ||
                !jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Http) ||
                !IsTrustedSource(ctx.Connection.RemoteIpAddress, additionalTrustedNetworks))
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

    /// <summary>
    /// True if the address is loopback, in an RFC1918 private range, or in any
    /// operator-supplied trusted network.
    /// </summary>
    private static bool IsTrustedSource(IPAddress? remoteIp, TrustedCidr[] additionalTrustedNetworks)
    {
        if (remoteIp is null) return false;
        if (IPAddress.IsLoopback(remoteIp)) return true;

        IPAddress ipv4 = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        if (ipv4.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (ipv4.TryWriteBytes(bytes, out _))
            {
                // RFC1918 private ranges: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
                if (bytes[0] == 10) return true;
                if (bytes[0] == 172 && (bytes[1] & 0xF0) == 16) return true;
                if (bytes[0] == 192 && bytes[1] == 168) return true;
            }
        }

        if (additionalTrustedNetworks.Length <= 0) return false;
        for (int i = 0; i < additionalTrustedNetworks.Length; i++)
        {
            if (additionalTrustedNetworks[i].Contains(remoteIp)) return true;
        }

        return false;
    }

    /// <summary>
    /// Parse CIDR strings once at startup so the per-request check stays
    /// allocation-free. Invalid entries are logged and skipped. IPv4 only.
    /// </summary>
    private static TrustedCidr[] ParseTrustedNetworks(string[] cidrs, ILogger logger)
    {
        if (cidrs is null || cidrs.Length == 0) return [];

        List<TrustedCidr> parsed = new(cidrs.Length);
        foreach (string? cidr in cidrs)
        {
            if (string.IsNullOrWhiteSpace(cidr)) continue;
            if (TrustedCidr.TryParse(cidr, out TrustedCidr network))
            {
                parsed.Add(network);
            }
            else if (logger.IsWarn)
            {
                logger.Warn($"Ignoring invalid CIDR in {nameof(IJsonRpcConfig.AdditionalTrustedNetworks)}: '{cidr}'");
            }
        }

        return parsed.ToArray();
    }

    /// <summary>
    /// IPv4 CIDR membership test. <see cref="Contains"/> is allocation-free.
    /// </summary>
    private readonly struct TrustedCidr
    {
        private readonly uint _network;
        private readonly uint _mask;

        private TrustedCidr(uint network, uint mask)
        {
            _network = network & mask;
            _mask = mask;
        }

        public bool Contains(IPAddress address)
        {
            IPAddress ipv4 = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (ipv4.AddressFamily != AddressFamily.InterNetwork) return false;

            Span<byte> bytes = stackalloc byte[4];
            if (!ipv4.TryWriteBytes(bytes, out _)) return false;
            uint ip = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            return (ip & _mask) == _network;
        }

        public static bool TryParse(string cidr, out TrustedCidr network)
        {
            network = default;
            int slash = cidr.IndexOf('/');
            if (slash < 1 || slash == cidr.Length - 1) return false;

            if (!IPAddress.TryParse(cidr.AsSpan(0, slash), out IPAddress? addr)) return false;
            if (addr.AddressFamily != AddressFamily.InterNetwork) return false;
            if (!int.TryParse(cidr.AsSpan(slash + 1), out int prefix) || prefix < 0 || prefix > 32) return false;

            Span<byte> bytes = stackalloc byte[4];
            if (!addr.TryWriteBytes(bytes, out _)) return false;
            uint ip = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            uint mask = prefix == 0 ? 0u : ~0u << (32 - prefix);
            network = new TrustedCidr(ip, mask);
            return true;
        }
    }

    internal static int GetStatusCode(in JsonRpcResult result)
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

    internal static bool IsResourceUnavailableError(JsonRpcResponse? response) => response is JsonRpcErrorResponse { Error.Code: ErrorCodes.ModuleTimeout }
                    or JsonRpcErrorResponse { Error.Code: ErrorCodes.LimitExceeded };

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
                    CountingWriter resultWriter = stream is not null ? new CountingStreamPipeWriter(stream) : new CountingPipeWriter(ctx.Response.BodyWriter);
                    try
                    {
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.StatusCode = GetStatusCode(result);

                        // Flush headers before body for unbuffered responses
                        if (stream is null)
                        {
                            if (!result.IsCollection &&
                                result.Response is not null &&
                                TryGetKnownSingleResponseContentLength(result.Response, out long contentLength))
                            {
                                ctx.Response.ContentLength = contentLength;
                            }

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
    internal static void WriteJsonRpcResponse(CountingWriter writer, JsonRpcResponse response)
    {
        if (response is JsonRpcSuccessResponse successResponse && TryWritePrimitiveSuccessResponse(writer, successResponse))
        {
            return;
        }

        WriteJsonRpcResponse((IBufferWriter<byte>)writer, response);
    }

    private static bool TryWritePrimitiveSuccessResponse(CountingWriter writer, JsonRpcSuccessResponse response)
    {
        if (ForcedNumberConversion.Value != NumberConversion.Hex || response.Result is not ulong result)
        {
            return false;
        }

        writer.Write("{\"jsonrpc\":\"2.0\",\"result\":"u8);
        WriteHexUlong(writer, result);
        writer.Write(",\"id\":"u8);
        WriteIdRaw(writer, response.Id);
        writer.Write("}"u8);
        return true;
    }

    private static void WriteHexUlong(PipeWriter writer, ulong value)
    {
        Span<byte> buffer = stackalloc byte[20];
        buffer[0] = (byte)'"';
        buffer[1] = (byte)'0';
        buffer[2] = (byte)'x';

        int offset = 3;
        bool hasNonZeroNibble = false;
        for (int shift = 60; shift >= 0; shift -= 4)
        {
            int nibble = (int)(value >> shift) & 0xF;
            if (nibble == 0 && !hasNonZeroNibble)
            {
                continue;
            }

            hasNonZeroNibble = true;
            buffer[offset++] = (byte)(nibble < 10
                ? (byte)'0' + nibble
                : (byte)'a' + nibble - 10);
        }

        if (!hasNonZeroNibble)
        {
            buffer[offset++] = (byte)'0';
        }

        buffer[offset++] = (byte)'"';
        writer.Write(buffer[..offset]);
    }

    private static void WriteJsonRpcResponse(IBufferWriter<byte> writer, JsonRpcResponse response)
    {
        using Utf8JsonWriter jsonWriter = new(writer, new JsonWriterOptions { SkipValidation = true });

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

    internal static bool TryGetKnownSingleResponseContentLength(JsonRpcResponse response, out long length)
    {
        if (response is JsonRpcSuccessResponse successResponse &&
            TryGetRawResultLength(successResponse.Result, out long resultLength) &&
            TryGetRawIdLength(response.Id, out int idLength))
        {
            length = "{\"jsonrpc\":\"2.0\",\"result\":"u8.Length +
                     resultLength +
                     ",\"id\":"u8.Length +
                     idLength +
                     "}"u8.Length;
            return true;
        }

        length = 0;
        return false;
    }

    private static bool TryGetRawResultLength(object? result, out long length)
    {
        switch (result)
        {
            case RawJsonRpcResult raw:
                length = raw.Json.Length;
                return true;
            case ulong value when ForcedNumberConversion.Value == NumberConversion.Hex:
                length = GetHexUlongLength(value);
                return true;
            default:
                length = 0;
                return false;
        }
    }

    private static int GetHexUlongLength(ulong value)
    {
        int digits = 1;
        ulong current = value;
        while ((current >>= 4) != 0)
        {
            digits++;
        }

        return 2 + 2 + digits;
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
        static void WriteOther(Utf8JsonWriter writer, object id) => JsonSerializer.Serialize(writer, id, id.GetType(), EthereumJsonSerializer.JsonOptions);
    }

    internal static async ValueTask WriteStreamableResponseAsync(
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
        static void WriteOther(PipeWriter writer, object? id)
        {
            switch (id)
            {
                case string strId:
                    {
                        // escaping is intentionally skipped for max performance;
                        // JSON-RPC IDs are usually simple values (typically numeric)
                        Span<byte> buf = writer.GetSpan(strId.Length * 3 + 2);
                        buf[0] = (byte)'"';
                        int len = Encoding.UTF8.GetBytes(strId, buf[1..]);
                        buf[len + 1] = (byte)'"';
                        writer.Advance(len + 2);
                        break;
                    }
                default:
                    {
                        writer.Write("null"u8);
                        break;
                    }
            }
        }
    }

    private static bool TryGetRawIdLength(object? id, out int length)
    {
        switch (id)
        {
            case int intId:
                length = GetFormattedIntLength(intId);
                return true;
            case long longId:
                length = GetFormattedLongLength(longId);
                return true;
            case string strId:
                length = Encoding.UTF8.GetByteCount(strId) + 2;
                return true;
            case null:
                length = "null"u8.Length;
                return true;
            default:
                length = 0;
                return false;
        }

        static int GetFormattedIntLength(int value)
        {
            Span<byte> buffer = stackalloc byte[11];
            value.TryFormat(buffer, out int written);
            return written;
        }

        static int GetFormattedLongLength(long value)
        {
            Span<byte> buffer = stackalloc byte[20];
            value.TryFormat(buffer, out int written);
            return written;
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

        public override void CancelPendingRead() => stream.CancelPendingRead();

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
