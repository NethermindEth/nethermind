// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NSubstitute;

namespace Nethermind.FastRpc.Benchmark;

/// <summary>
/// Compares the isolated fast transport against Nethermind JSON-RPC and REST-style baselines
/// using the same routes and the same simple, complex, and big response objects.
/// </summary>
[MemoryDiagnoser]
public class FastRpcTransportBenchmarks : IDisposable
{
    private const int BatchSize = 4;
    private const string Json = "application/json";
    private const string Ssz = "application/octet-stream";

    private static readonly byte[] JwtSecret = "nethermind-fast-rpc-benchmark-secret"u8.ToArray();
    private static readonly AuthenticationHeaderValue Authorization =
        new("Bearer", FastJwt.CreateHmacSha256Token(JwtSecret));

    [Params(BenchmarkPayloads.Simple, BenchmarkPayloads.Complex, BenchmarkPayloads.Big)]
    public string Payload = BenchmarkPayloads.Simple;

    private BenchmarkPayload _payload = null!;
    private byte[] _jsonRpcRequest = null!;
    private byte[] _jsonRpcBatchRequest = null!;
    private IHost _fastHost = null!;
    private IHost _nethermindJsonRpcHost = null!;
    private IHost _nethermindRestHost = null!;
    private HttpClient _fastClient = null!;
    private HttpClient _nethermindJsonRpcClient = null!;
    private HttpClient _nethermindRestClient = null!;
    private ClientWebSocket _fastWebSocket = null!;
    private ClientWebSocket _nethermindWebSocket = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        BenchmarkPayload[] payloads = BenchmarkPayloads.CreateAll();
        _payload = SelectPayload(payloads, Payload);
        _jsonRpcRequest = BenchmarkPayloads.BuildJsonRpcRequest(_payload.Name);
        _jsonRpcBatchRequest = BenchmarkPayloads.BuildJsonRpcBatchRequest(_payload.Name, BatchSize);

        _fastHost = BuildFastHost(payloads);
        _nethermindJsonRpcHost = BuildNethermindJsonRpcHost(payloads);
        _nethermindRestHost = BuildNethermindRestHost(payloads);

        Uri fastUri = GetServerUri(_fastHost);
        Uri nethermindJsonRpcUri = GetServerUri(_nethermindJsonRpcHost);
        Uri nethermindRestUri = GetServerUri(_nethermindRestHost);
        _fastClient = CreateHttpClient(fastUri);
        _nethermindJsonRpcClient = CreateHttpClient(nethermindJsonRpcUri);
        _nethermindRestClient = CreateHttpClient(nethermindRestUri);

        _fastWebSocket = new ClientWebSocket();
        _fastWebSocket.Options.Proxy = null;
        _fastWebSocket.Options.SetRequestHeader("Authorization", Authorization.ToString());
        await _fastWebSocket.ConnectAsync(ToWebSocketUri(fastUri), CancellationToken.None);

        _nethermindWebSocket = new ClientWebSocket();
        _nethermindWebSocket.Options.Proxy = null;
        await _nethermindWebSocket.ConnectAsync(ToWebSocketUri(nethermindJsonRpcUri), CancellationToken.None);
    }

    [GlobalCleanup]
    public void GlobalCleanup() => Dispose();

    public void Dispose()
    {
        _fastWebSocket?.Dispose();
        _nethermindWebSocket?.Dispose();
        _fastClient?.Dispose();
        _nethermindJsonRpcClient?.Dispose();
        _nethermindRestClient?.Dispose();
        _fastHost?.Dispose();
        _nethermindJsonRpcHost?.Dispose();
        _nethermindRestHost?.Dispose();
    }

    [Benchmark(Description = "Fast REST JSON GET")]
    public Task<int> FastRestJsonGet() =>
        SendRestAsync(_fastClient, HttpMethod.Get, Json, requestBody: null, authorize: true);

    [Benchmark(Description = "Nethermind REST JSON GET")]
    public Task<int> NethermindRestJsonGet() =>
        SendRestAsync(_nethermindRestClient, HttpMethod.Get, Json, requestBody: null, authorize: false);

    [Benchmark(Description = "Fast REST JSON POST")]
    public Task<int> FastRestJsonPost() =>
        SendRestAsync(_fastClient, HttpMethod.Post, Json, _payload.Json, authorize: true);

    [Benchmark(Description = "Nethermind REST JSON POST")]
    public Task<int> NethermindRestJsonPost() =>
        SendRestAsync(_nethermindRestClient, HttpMethod.Post, Json, _payload.Json, authorize: false);

    [Benchmark(Description = "Fast REST SSZ GET")]
    public Task<int> FastRestSszGet() =>
        SendRestAsync(_fastClient, HttpMethod.Get, Ssz, requestBody: null, authorize: true);

    [Benchmark(Description = "Nethermind REST SSZ GET")]
    public Task<int> NethermindRestSszGet() =>
        SendRestAsync(_nethermindRestClient, HttpMethod.Get, Ssz, requestBody: null, authorize: false);

    [Benchmark(Description = "Fast REST SSZ POST")]
    public Task<int> FastRestSszPost() =>
        SendRestAsync(_fastClient, HttpMethod.Post, Ssz, _payload.Ssz, authorize: true);

    [Benchmark(Description = "Nethermind REST SSZ POST")]
    public Task<int> NethermindRestSszPost() =>
        SendRestAsync(_nethermindRestClient, HttpMethod.Post, Ssz, _payload.Ssz, authorize: false);

    [Benchmark(Description = "Fast JSON-RPC single")]
    public Task<int> FastJsonRpcSingle() =>
        SendFastJsonRpcAsync(_jsonRpcRequest);

    [Benchmark(Description = "Nethermind JSON-RPC single", Baseline = true)]
    public Task<int> NethermindJsonRpcSingle() =>
        SendNethermindJsonRpcAsync(_jsonRpcRequest);

    [Benchmark(Description = "Fast JSON-RPC batch")]
    public Task<int> FastJsonRpcBatch() =>
        SendFastJsonRpcAsync(_jsonRpcBatchRequest);

    [Benchmark(Description = "Nethermind JSON-RPC batch")]
    public Task<int> NethermindJsonRpcBatch() =>
        SendNethermindJsonRpcAsync(_jsonRpcBatchRequest);

    [Benchmark(Description = "Fast JSON-RPC websocket")]
    public async Task<int> FastJsonRpcWebSocket()
    {
        await _fastWebSocket.SendAsync(_jsonRpcRequest, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        return await ReceiveWebSocketResponseAsync(_fastWebSocket);
    }

    [Benchmark(Description = "Nethermind JSON-RPC websocket")]
    public async Task<int> NethermindJsonRpcWebSocket()
    {
        await _nethermindWebSocket.SendAsync(_jsonRpcRequest, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        return await ReceiveWebSocketResponseAsync(_nethermindWebSocket);
    }

    private async Task<int> SendRestAsync(
        HttpClient client,
        HttpMethod method,
        string mediaType,
        byte[]? requestBody,
        bool authorize)
    {
        using HttpRequestMessage request = new(method, $"/{_payload.Name}");
        if (authorize)
        {
            request.Headers.Authorization = Authorization;
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));

        if (requestBody is not null)
        {
            ByteArrayContent content = new(requestBody);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            request.Content = content;
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        return body.Length;
    }

    private async Task<int> SendFastJsonRpcAsync(byte[] requestBody)
    {
        using ByteArrayContent content = new(requestBody);
        content.Headers.ContentType = new MediaTypeHeaderValue(Json);

        using HttpRequestMessage request = new(HttpMethod.Post, "/") { Content = content };
        request.Headers.Authorization = Authorization;

        using HttpResponseMessage response = await _fastClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        return body.Length;
    }

    private async Task<int> SendNethermindJsonRpcAsync(byte[] requestBody)
    {
        using ByteArrayContent content = new(requestBody);
        content.Headers.ContentType = new MediaTypeHeaderValue(Json);

        using HttpResponseMessage response = await _nethermindJsonRpcClient.PostAsync("/", content);
        response.EnsureSuccessStatusCode();
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        return body.Length;
    }

    private static IHost BuildFastHost(BenchmarkPayload[] payloads)
    {
        FastRpcRouter router = new();
        for (int i = 0; i < payloads.Length; i++)
        {
            BenchmarkPayload payload = payloads[i];
            router.Map(payload.Name, (_, _) =>
                ValueTask.FromResult(new FastRpcResponse(payload.Json, payload.Ssz)));
        }

        FastRpcApplication appDelegate = router.Build(new FastRpcOptions { JwtSecret = JwtSecret });

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(static builder => builder.ClearProviders())
            .ConfigureWebHostDefaults(web =>
            {
                web.UseKestrel(static options => options.AddServerHeader = false);
                web.UseUrls(GetAvailableUrl());
                web.Configure(app =>
                {
                    app.UseWebSockets();
                    app.Run(appDelegate.InvokeAsync);
                });
            })
            .Build();

        host.Start();
        return host;
    }

    private static IHost BuildNethermindRestHost(BenchmarkPayload[] payloads)
    {
        Dictionary<string, BenchmarkPayload> payloadMap = BuildPayloadMap(payloads);

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(static builder => builder.ClearProviders())
            .ConfigureWebHostDefaults(web =>
            {
                web.UseKestrel(static options => options.AddServerHeader = false);
                web.UseUrls(GetAvailableUrl());
                web.Configure(app => app.Run(async context =>
                {
                    if ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsPost(context.Request.Method)) ||
                        !TryGetRoute(context, out string? route) ||
                        !payloadMap.TryGetValue(route, out BenchmarkPayload? payload))
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    if (HttpMethods.IsPost(context.Request.Method) && context.Request.ContentLength is > 0)
                    {
                        await DrainRequestAsync(context.Request.BodyReader, context.RequestAborted);
                    }

                    bool wantsSsz = WantsSsz(context);
                    ReadOnlyMemory<byte> body = wantsSsz ? payload.Ssz : payload.Json;
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = wantsSsz ? Ssz : Json;
                    context.Response.BodyWriter.Write(body.Span);
                    await context.Response.BodyWriter.FlushAsync(context.RequestAborted);
                }));
            })
            .Build();

        host.Start();
        return host;
    }

    private static IHost BuildNethermindJsonRpcHost(BenchmarkPayload[] payloads)
    {
        IJsonRpcService service = new BenchmarkJsonRpcService(payloads);
        JsonRpcConfig config = new();
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        JsonRpcProcessor processor = new(service, config, fileSystem, LimboLogs.Instance);
        EthereumJsonSerializer serializer = new();

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(static builder => builder.ClearProviders())
            .ConfigureWebHostDefaults(web =>
            {
                web.UseKestrel(static options => options.AddServerHeader = false);
                web.UseUrls(GetAvailableUrl());
                web.Configure(app =>
                {
                    app.UseWebSockets();
                    app.Run(async context =>
                    {
                        if (context.WebSockets.IsWebSocketRequest)
                        {
                            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                            using WebSocketMessageStream stream = new(socket, LimboLogs.Instance);
                            using JsonRpcSocketsClient<WebSocketMessageStream> client = new(
                                "benchmark",
                                stream,
                                RpcEndpoint.Ws,
                                processor,
                                new NullJsonRpcLocalStats(),
                                serializer);
                            await client.ReceiveLoopAsync(context.RequestAborted);
                            return;
                        }

                        using JsonRpcContext rpcContext = new(RpcEndpoint.Http);
                        await foreach (JsonRpcResult result in processor.ProcessAsync(context.Request.BodyReader, rpcContext))
                        {
                            using (result)
                            {
                                context.Response.StatusCode = 200;
                                context.Response.ContentType = Json;
                                await WriteNethermindJsonRpcResultAsync(
                                    context.Response.BodyWriter,
                                    serializer,
                                    result,
                                    context.RequestAborted);
                                await context.Response.CompleteAsync();
                            }
                            break;
                        }
                    });
                });
            })
            .Build();

        host.Start();
        return host;
    }

    private static HttpClient CreateHttpClient(Uri baseAddress)
    {
        SocketsHttpHandler handler = new()
        {
            MaxConnectionsPerServer = int.MaxValue,
            UseProxy = false,
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
        };
    }

    private static Uri GetServerUri(IHost host)
    {
        IServer server = host.Services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not publish a bound address.");

        foreach (string address in addresses.Addresses)
        {
            return new Uri(address);
        }

        throw new InvalidOperationException("Kestrel did not publish a bound address.");
    }

    private static Uri ToWebSocketUri(Uri httpUri)
    {
        UriBuilder builder = new(httpUri)
        {
            Scheme = string.Equals(httpUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ? "wss" : "ws",
            Path = "ws",
        };

        return builder.Uri;
    }

    private static string GetAvailableUrl()
    {
        using TcpListener listener = new(IPAddress.Loopback, port: 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return $"http://127.0.0.1:{port}";
    }

    private static async Task WriteNethermindJsonRpcResultAsync(
        PipeWriter writer,
        EthereumJsonSerializer serializer,
        JsonRpcResult result,
        CancellationToken cancellationToken)
    {
        if (!result.IsCollection)
        {
            await serializer.SerializeAsync(writer, result.Response!);
            return;
        }

        using Utf8JsonWriter json = new(writer);
        json.WriteStartArray();
        await foreach (JsonRpcResult.Entry entry in result.BatchedResponses.WithCancellation(cancellationToken))
        {
            try
            {
                JsonSerializer.Serialize(json, entry.Response, EthereumJsonSerializer.JsonOptions);
            }
            finally
            {
                entry.Dispose();
            }
        }
        json.WriteEndArray();
        await json.FlushAsync(cancellationToken);
    }

    private static async Task<int> ReceiveWebSocketResponseAsync(WebSocket socket)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int received = 0;

        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                received += result.Count;
                if (result.EndOfMessage) return received;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Dictionary<string, BenchmarkPayload> BuildPayloadMap(BenchmarkPayload[] payloads)
    {
        Dictionary<string, BenchmarkPayload> map = new(StringComparer.Ordinal);
        for (int i = 0; i < payloads.Length; i++)
        {
            map[payloads[i].Name] = payloads[i];
        }

        return map;
    }

    private static bool TryGetRoute(HttpContext context, [NotNullWhen(true)] out string? route)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        if (path.Length <= 1 || path[0] != '/')
        {
            route = null;
            return false;
        }

        route = path[1..];
        return route.Length > 0;
    }

    private static bool WantsSsz(HttpContext context)
    {
        foreach (string? accept in context.Request.Headers.Accept)
        {
            if (accept?.Contains(Ssz, StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return context.Request.ContentType?.Contains(Ssz, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static async Task DrainRequestAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) return;
        }
    }

    private static BenchmarkPayload SelectPayload(BenchmarkPayload[] payloads, string name)
    {
        for (int i = 0; i < payloads.Length; i++)
        {
            if (string.Equals(payloads[i].Name, name, StringComparison.Ordinal))
            {
                return payloads[i];
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown benchmark payload.");
    }
}
