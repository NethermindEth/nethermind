// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Primitives;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Authentication;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Runner.Monitoring;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.Runner.JsonRpc;

public class Startup : IStartup
{
    private const string ApplicationJsonContentType = "application/json";
    private static readonly StringValues JsonContentTypeHeader = new(ApplicationJsonContentType);

    private JsonRpcProcessor _jsonRpcProcessor = null!;
    private JsonRpcService _jsonRpcService = null!;
    private IJsonRpcLocalStats _jsonRpcLocalStats = null!;
    private EthereumJsonSerializer _jsonSerializer = null!;
    private IJsonRpcConfig _jsonRpcConfig = null!;
    private IRpcAuthentication? _rpcAuthentication;
    private ILogger _logger = default;

    public Startup() { }

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

        IJsonRpcUrlCollection? urlCollection = sp.GetService<IJsonRpcUrlCollection>();
        HashSet<int> engineApiPorts = urlCollection is null
            ? []
            : urlCollection.Values
                .Where(static u => u.IsAuthenticated)
                .Select(static u => u.Port)
                .ToHashSet();

        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = jsonRpcConfig.MaxRequestBodySize;
            options.ConfigureHttpsDefaults(co => co.SslProtocols |= SslProtocols.Tls13);

            options.Limits.Http2.InitialConnectionWindowSize = (int)1.MiB;
            options.Limits.Http2.InitialStreamWindowSize = (int)1.MiB;

            options.ConfigureEndpointDefaults(listenOptions =>
            {
                int port = (listenOptions.EndPoint as IPEndPoint)?.Port ?? 0;
                if (engineApiPorts.Contains(port))
                {
                    // Keep HTTP/1.1 + HTTP/2 on the engine port: SSZ-REST uses HTTP/2, while legacy
                    // Engine API JSON-RPC still relies on HTTP/1.1 and shares the same listener.
                    listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                }
                else
                {
                    listenOptions.Protocols = HttpProtocols.Http1;
                    listenOptions.DisableAltSvcHeader = true;
                }
            });
        });
        services.Configure<SocketTransportOptions>(options =>
        {
            options.IOQueueCount = 0;
            options.WaitForDataBeforeAllocatingBuffer = false;
            options.UnsafePreferInlineScheduling = true;
        });
        Bootstrap.Instance.RegisterJsonRpcServices(services);

        services.AddSingleton<MatcherPolicy, LocalPortMatcherPolicy>();

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
        EthereumJsonSerializer.AddTypeInfoResolver(FacadeJsonContext.Default, JsonTypeInfoResolverPriority.Facade);
        EthereumJsonSerializer.AddTypeInfoResolver(EthRpcJsonContext.Default, JsonTypeInfoResolverPriority.EthRpc);
        EthereumJsonSerializer.AddTypeInfoResolver(JsonRpcResponseJsonContext.Default, JsonTypeInfoResolverPriority.JsonRpcResponse);

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

        TrustedCidr[] additionalTrustedNetworks = ParseTrustedNetworks(jsonRpcConfig.AdditionalTrustedNetworks, logger);

        // Trusted JSON-RPC HTTP POSTs dispatch directly, skipping routing, CORS,
        // compression, and WebSocket middleware. Authentication is still enforced
        // inside the handler for authenticated endpoints.
        app.Use((ctx, next) =>
        {
            if (!TryGetTrustedHttpJsonRpcUrl(ctx, jsonRpcUrlCollection, additionalTrustedNetworks, out JsonRpcUrl? jsonRpcUrl))
            {
                return next();
            }

            return ProcessJsonRpcRequestCoreAsync(ctx, jsonRpcUrl);
        });

        app.UseRouting();
        app.UseCors();

        // Skip response compression for localhost (low benefit, high allocation cost)
        // and for Engine API requests (latency-sensitive consensus path)
        app.UseWhen(ctx =>
            !IsLocalhost(ctx.Connection.RemoteIpAddress!) &&
            !(jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl url) && url.IsAuthenticated),
            builder => builder.UseResponseCompression());

        app.Use((ctx, next) => HandleJsonRpcHttpRequestAsync(ctx, next, jsonRpcUrlCollection));

        if (initConfig.WebSocketsEnabled)
        {
            app.UseWebSockets(new WebSocketOptions());
            app.UseWhen(ctx =>
                ctx.WebSockets.IsWebSocketRequest &&
                jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl jsonRpcUrl) &&
                jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Ws),
            builder => builder.UseWebSocketsModules());
        }

        IReadOnlySet<int> healthPorts = jsonRpcUrlCollection.Values
            .Where(url => url.IsModuleEnabled(ModuleType.Health))
            .Select(url => url.Port)
            .ToHashSet();

        app.UseEndpoints(endpoints =>
        {
            if (healthChecksConfig.Enabled && healthPorts.Count > 0)
            {
                try
                {
                    endpoints.MapHealthChecks(healthChecksConfig.Slug, new HealthCheckOptions()
                    {
                        Predicate = _ => true,
                        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
                    }).RequireLocalPort(healthPorts);
                    if (healthChecksConfig.UIEnabled)
                    {
                        endpoints.MapHealthChecksUI(setup => setup.AddCustomStylesheet(Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, "nethermind.css")))
                            .RequireLocalPort(healthPorts);
                    }
                    endpoints.MapDataFeeds(lifetime).RequireLocalPort(healthPorts);
                }
                catch (Exception e)
                {
                    if (logger.IsError) logger.Error("Unable to initialize health checks. Check if you have Nethermind.HealthChecks.dll in your plugins folder.", e);
                }
            }
        });

        if (healthChecksConfig.Enabled && healthPorts.Count > 0)
        {
            ManifestEmbeddedFileProvider fileProvider = new(typeof(Startup).Assembly, "wwwroot");

            app.UseWhen(
                ctx => healthPorts.Contains(ctx.Connection.LocalPort),
                builder =>
                {
                    builder.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
                    builder.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
                });
        }
    }

    private static bool IsLocalhost(IPAddress remoteIp)
        => IPAddress.IsLoopback(remoteIp);

    internal static bool TryGetTrustedHttpJsonRpcUrl(
        HttpContext ctx,
        IJsonRpcUrlCollection jsonRpcUrlCollection,
        TrustedCidr[] additionalTrustedNetworks,
        [NotNullWhen(true)] out JsonRpcUrl? jsonRpcUrl)
    {
        if (ctx.Request.Method == "POST" &&
            jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out jsonRpcUrl) &&
            jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Http) &&
            IsTrustedSource(ctx, additionalTrustedNetworks) &&
            IsJsonContentType(ctx.Request.ContentType))
        {
            return true;
        }

        jsonRpcUrl = null;
        return false;
    }

    internal Task HandleJsonRpcHttpRequestAsync(HttpContext ctx, Func<Task> next, IJsonRpcUrlCollection jsonRpcUrlCollection)
    {
        if (ctx.GetEndpoint() is not null)
        {
            return next();
        }

        if (!IsJsonContentType(ctx.Request.ContentType))
        {
            return next();
        }

        string method = ctx.Request.Method;
        if (method is not "POST" and not "GET")
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        }

        if (!jsonRpcUrlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl jsonRpcUrl) || !jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Http))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        if (method == "GET" && ctx.Request.Headers.Accept.Count > 0 &&
            !ctx.Request.Headers.Accept[0]!.Contains("text/html", StringComparison.Ordinal))
        {
            return ctx.Response.WriteAsync("Nethermind JSON RPC");
        }

        return ProcessJsonRpcRequestCoreAsync(ctx, jsonRpcUrl);
    }

    internal static bool IsJsonContentType(string? contentType) =>
        contentType is not null &&
        contentType.StartsWith(ApplicationJsonContentType, StringComparison.OrdinalIgnoreCase) &&
        (contentType.Length == ApplicationJsonContentType.Length ||
         contentType[ApplicationJsonContentType.Length] == ';');

    internal static bool IsTrustedSource(HttpContext ctx, TrustedCidr[] additionalTrustedNetworks)
    {
        TrustedSourceFeature? trustedSourceFeature = ctx.Features.Get<TrustedSourceFeature>();
        if (trustedSourceFeature is not null)
        {
            return trustedSourceFeature.IsTrusted;
        }

        bool isTrusted = IsTrustedSource(ctx.Connection.RemoteIpAddress, additionalTrustedNetworks);
        ctx.Features.Set(new TrustedSourceFeature(isTrusted));
        return isTrusted;
    }

    internal static bool IsTrustedSource(IPAddress? remoteIp, TrustedCidr[] additionalTrustedNetworks)
    {
        if (remoteIp is null) return false;
        if (IPAddress.IsLoopback(remoteIp)) return true;

        IPAddress ipv4 = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        if (ipv4.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (ipv4.TryWriteBytes(bytes, out _))
            {
                if (bytes[0] == 10) return true;
                if (bytes[0] == 172 && (bytes[1] & 0xF0) == 16) return true;
                if (bytes[0] == 192 && bytes[1] == 168) return true;
            }
        }

        for (int i = 0; i < additionalTrustedNetworks.Length; i++)
        {
            if (additionalTrustedNetworks[i].Contains(remoteIp)) return true;
        }

        return false;
    }

    internal static TrustedCidr[] ParseTrustedNetworks(string[]? cidrs, ILogger logger)
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

    private sealed class TrustedSourceFeature(bool isTrusted) { public bool IsTrusted { get; } = isTrusted; }

    internal readonly struct TrustedCidr
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

    private async Task PushErrorResponseAsync(HttpContext ctx, int statusCode, int errorCode, string message)
    {
        ctx.Response.Headers.ContentType = JsonContentTypeHeader;
        ctx.Response.StatusCode = statusCode;
        JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(errorCode, message);
        await _jsonSerializer.SerializeAsync(ctx.Response.BodyWriter, response);
        await ctx.Response.CompleteAsync();
    }

    internal async Task ProcessJsonRpcRequestCoreAsync(HttpContext ctx, JsonRpcUrl jsonRpcUrl)
    {
        long startTime = _jsonRpcLocalStats.IsEnabled ? Stopwatch.GetTimestamp() : 0;

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

        long? contentLength = ctx.Request.ContentLength;
        long? effectiveMaxRequestBodySize = jsonRpcUrl.MaxRequestBodySize ?? _jsonRpcConfig.MaxRequestBodySize;
        CollectedHttpBody collectedBody = new();
        HttpJsonRpcResponseSink? responseSink = null;
        try
        {
            await CollectHttpRequestBodyAsync(ctx, contentLength, effectiveMaxRequestBodySize, collectedBody, ctx.RequestAborted);
            using JsonRpcContext jsonRpcContext = JsonRpcContext.Http(jsonRpcUrl);
            responseSink = new HttpJsonRpcResponseSink(ctx, jsonRpcUrl, _jsonRpcConfig, _jsonRpcLocalStats, _logger, startTime);

            await _jsonRpcProcessor.ProcessAsync(
                collectedBody.Memory,
                jsonRpcContext,
                responseSink,
                new JsonRpcProcessingOptions(JsonRpcInputMode.SingleDocument),
                ctx.RequestAborted);
        }
        catch (Exception e) when (e is OperationCanceledException || e.InnerException is OperationCanceledException)
        {
            JsonRpcErrorResponse error = _jsonRpcService.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
            responseSink ??= new HttpJsonRpcResponseSink(ctx, jsonRpcUrl, _jsonRpcConfig, _jsonRpcLocalStats, _logger, startTime);
            await responseSink.WriteSingleAsync(error, RpcReport.Error, ctx.RequestAborted);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
        {
            responseSink = null;
            if (_logger.IsDebug) LogBadRequest(_logger, e);
            ctx.Response.Headers.ContentType = JsonContentTypeHeader;
            ctx.Response.StatusCode = e.StatusCode;
            JsonRpcErrorResponse errResp = _jsonRpcService.GetErrorResponse(
                e.StatusCode == StatusCodes.Status413PayloadTooLarge ? ErrorCodes.LimitExceeded : ErrorCodes.InvalidRequest,
                e.Message);
            await _jsonSerializer.SerializeAsync(ctx.Response.BodyWriter, errResp);
            await ctx.Response.CompleteAsync();
        }
        finally
        {
            try
            {
                if (responseSink is not null)
                {
                    await responseSink.CompleteAsync(ctx.RequestAborted);
                }

                Interlocked.Add(ref Metrics.JsonRpcBytesReceivedHttp, collectedBody.BytesRead);
            }
            finally
            {
                collectedBody.Dispose();
            }
        }
    }

    private static async ValueTask CollectHttpRequestBodyAsync(
        HttpContext context,
        long? contentLength,
        long? maxRequestBodySize,
        CollectedHttpBody collectedBody,
        CancellationToken cancellationToken)
    {
        const int MissingContentLengthInitialCapacity = 4096;

        if (contentLength is > 0 &&
            maxRequestBodySize is not null &&
            contentLength.Value > maxRequestBodySize.Value)
        {
            ThrowRequestBodyTooLarge(maxRequestBodySize.Value);
        }

        if (contentLength is > 0 and <= int.MaxValue)
        {
            collectedBody.EnsureExactCapacity((int)contentLength.Value);
        }
        else if (contentLength is null or > int.MaxValue)
        {
            collectedBody.SetInitialCapacity(MissingContentLengthInitialCapacity);
        }

        PipeReader bodyReader = context.Request.BodyReader;
        try
        {
            while (true)
            {
                ReadResult readResult = await bodyReader.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = readResult.Buffer;

                long newBytesRead = collectedBody.BytesRead + buffer.Length;
                if (maxRequestBodySize is not null && newBytesRead > maxRequestBodySize)
                {
                    ThrowRequestBodyTooLarge(maxRequestBodySize.Value);
                }

                if (newBytesRead > int.MaxValue)
                {
                    ThrowRequestBodyTooLarge(maxRequestBodySize ?? int.MaxValue);
                }

                collectedBody.Append(buffer);
                bodyReader.AdvanceTo(buffer.End);

                if (readResult.IsCompleted || readResult.IsCanceled)
                {
                    break;
                }
            }
        }
        finally
        {
            await bodyReader.CompleteAsync();
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowRequestBodyTooLarge(long maxRequestBodySize) =>
            throw new Microsoft.AspNetCore.Http.BadHttpRequestException(
                $"Request body too large. The max request body size is {maxRequestBodySize} bytes.",
                StatusCodes.Status413PayloadTooLarge);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LogBadRequest(ILogger logger, Exception e) =>
        logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");

    private sealed class CollectedHttpBody : IDisposable
    {
        private const int DefaultInitialCapacity = 256;

        private byte[]? _buffer;
        private int _initialCapacity = DefaultInitialCapacity;

        public int BytesRead { get; private set; }

        public ReadOnlyMemory<byte> Memory =>
            _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, BytesRead);

        public void SetInitialCapacity(int initialCapacity)
        {
            if (_buffer is null && initialCapacity > _initialCapacity)
            {
                _initialCapacity = initialCapacity;
            }
        }

        public void EnsureExactCapacity(int capacity)
        {
            if (capacity <= 0 || _buffer?.Length >= capacity)
            {
                return;
            }

            RentCapacity(capacity);
        }

        public void EnsureCapacity(int minCapacity)
        {
            if (minCapacity <= 0 || _buffer?.Length >= minCapacity)
            {
                return;
            }

            int newCapacity = _buffer is null ? _initialCapacity : _buffer.Length;
            while (newCapacity < minCapacity)
            {
                newCapacity = newCapacity <= Array.MaxLength / 2 ? newCapacity * 2 : minCapacity;
            }

            RentCapacity(newCapacity);
        }

        private void RentCapacity(int newCapacity)
        {
            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
            if (_buffer is not null)
            {
                _buffer.AsSpan(0, BytesRead).CopyTo(newBuffer);
                ArrayPool<byte>.Shared.Return(_buffer);
            }

            _buffer = newBuffer;
        }

        public void Append(ReadOnlySequence<byte> sequence)
        {
            int sequenceLength = (int)sequence.Length;
            if (sequenceLength == 0)
            {
                return;
            }

            int newLength = BytesRead + sequenceLength;
            EnsureCapacity(newLength);
            sequence.CopyTo(_buffer!.AsSpan(BytesRead, sequenceLength));
            BytesRead = newLength;
        }

        public void Dispose()
        {
            if (_buffer is null)
            {
                return;
            }

            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
            BytesRead = 0;
        }
    }
}
