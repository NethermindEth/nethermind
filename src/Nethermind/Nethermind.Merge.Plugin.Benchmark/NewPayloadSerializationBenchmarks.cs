// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Merge.Plugin.SszRest.Handlers;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Ssz;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Benchmark;

/// <summary>
/// Compares SSZ-REST and JSON-RPC paths for engine_newPayloadV3 across three layers:
/// pure decode, pure encode of <see cref="PayloadStatusV1"/>, and a full Kestrel round-trip
/// through the production <see cref="SszMiddleware"/> / <see cref="JsonRpcProcessor"/>.
/// Both round-trip paths dispatch through the same <see cref="IEngineRpcModule"/> stub that
/// returns VALID immediately, so the difference is transport + codec, not consensus work.
/// </summary>
[MemoryDiagnoser]
public class NewPayloadSerializationBenchmarks : IDisposable
{
    private const int EnginePort = 8551;
    private const string BearerToken =
        "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9" +
        ".eyJpYXQiOjE3MDAwMDAwMDB9" +
        ".stubTokenForBenchmarkingOnly";

    private static readonly MediaTypeHeaderValue OctetStream = new("application/octet-stream");
    private static readonly MediaTypeHeaderValue ApplicationJson = new("application/json");
    private static readonly AuthenticationHeaderValue Authorization = AuthenticationHeaderValue.Parse(BearerToken);
    private static readonly MediaTypeWithQualityHeaderValue OctetAccept = new("application/octet-stream");

    private static readonly EthereumJsonSerializer Serializer = new();
    private static readonly ResultWrapper<PayloadStatusV1> ValidResult =
        ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid });
    private static readonly Task<ResultWrapper<PayloadStatusV1>> ValidTask = Task.FromResult(ValidResult);

    [Params(0, 1, 3, 6, 12, 24, 36, 72)]
    public int Blobs;

    // Heavy-block baseline: 250 × 600 B ≈ 150 KB of tx data — matches a typical
    // mainnet block. Bump for worst-case stress (~2500 × 600 B ≈ 1.5 MB).
    private const int Txs = 250;

    private IHost _sszHost = null!;
    private IHost _jsonHost = null!;
    private TestServer _sszServer = null!;
    private TestServer _jsonServer = null!;
    private HttpClient _sszClient = null!;
    private HttpClient _jsonClient = null!;
    private byte[] _sszBody = null!;
    private byte[] _jsonBody = null!;
    private byte[] _jsonPayloadOnly = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        ExecutionPayloadV3 payload = BuildMaxBlobPayload(Blobs, Txs);
        Hash256[] blobHashes = BuildBlobVersionedHashes(Blobs);
        Hash256 parentRoot = TestItem.KeccakA;

        _sszBody = EncodeSszBody(payload, blobHashes, parentRoot);
        _jsonBody = EncodeJsonBody(payload, blobHashes, parentRoot);
        _jsonPayloadOnly = JsonSerializer.SerializeToUtf8Bytes(payload, EthereumJsonSerializer.JsonOptions);

        IEngineRpcModule engine = BuildEngineStub();
        _sszHost = BuildSszServer(engine);
        _jsonHost = BuildJsonServer(engine);
        _sszServer = _sszHost.GetTestServer();
        _jsonServer = _jsonHost.GetTestServer();
        _sszClient = _sszServer.CreateClient();
        _jsonClient = _jsonServer.CreateClient();
    }

    [GlobalCleanup]
    public void GlobalCleanup() => Dispose();

    public void Dispose()
    {
        _sszClient?.Dispose();
        _jsonClient?.Dispose();
        _sszServer?.Dispose();
        _jsonServer?.Dispose();
        _sszHost?.Dispose();
        _jsonHost?.Dispose();
    }

    [Benchmark(Description = "SSZ  decode NewPayloadV3 wire")]
    public NewPayloadV3RequestWire DeserializeSsz()
    {
        NewPayloadV3RequestWire.Decode(new ReadOnlySequence<byte>(_sszBody), out NewPayloadV3RequestWire wire);
        return wire;
    }

    [Benchmark(Description = "JSON decode ExecutionPayloadV3", Baseline = true)]
    public ExecutionPayloadV3 DeserializeJson() =>
        Serializer.Deserialize<ExecutionPayloadV3>(_jsonPayloadOnly)!;

    [Benchmark(Description = "SSZ  encode PayloadStatus")]
    public int SerializeSszPayloadStatus()
    {
        ArrayBufferWriter<byte> writer = new(64);
        return SszCodec.EncodePayloadStatus(ValidResult.Data!, writer);
    }

    [Benchmark(Description = "JSON encode PayloadStatus")]
    public byte[] SerializeJsonPayloadStatus() =>
        JsonSerializer.SerializeToUtf8Bytes(ValidResult.Data, EthereumJsonSerializer.JsonOptions);

    [Benchmark(Description = "SSZ  NewPayloadV3 (full Kestrel round-trip)")]
    public async Task<int> SszNewPayloadV3_RoundTrip()
    {
        using ByteArrayContent content = new(_sszBody);
        content.Headers.ContentType = OctetStream;

        using HttpRequestMessage req = new(HttpMethod.Post, "/engine/v3/payloads") { Content = content };
        req.Headers.Authorization = Authorization;
        req.Headers.Accept.Add(OctetAccept);

        using HttpResponseMessage response = await _sszClient.SendAsync(req);
        response.EnsureSuccessStatusCode();
        return (int)response.StatusCode;
    }

    [Benchmark(Description = "JSON NewPayloadV3 (full Kestrel round-trip)")]
    public async Task<int> JsonNewPayloadV3_RoundTrip()
    {
        using ByteArrayContent content = new(_jsonBody);
        content.Headers.ContentType = ApplicationJson;

        using HttpRequestMessage req = new(HttpMethod.Post, "/") { Content = content };
        req.Headers.Authorization = Authorization;

        using HttpResponseMessage response = await _jsonClient.SendAsync(req);
        response.EnsureSuccessStatusCode();
        return (int)response.StatusCode;
    }

    private const int CapellaMaxWithdrawals = 16;

    private static ExecutionPayloadV3 BuildMaxBlobPayload(int blobs, int txs) => new()
    {
        ParentHash = TestItem.KeccakA,
        FeeRecipient = TestItem.AddressA,
        StateRoot = TestItem.KeccakB,
        ReceiptsRoot = TestItem.KeccakC,
        LogsBloom = Bloom.Empty,
        PrevRandao = TestItem.KeccakD,
        BlockNumber = 20_000_000,
        GasLimit = 30_000_000,
        GasUsed = 15_000_000,
        Timestamp = 1_700_100_000,
        ExtraData = new byte[32],
        BaseFeePerGas = 10_000_000_000,
        BlockHash = TestItem.KeccakE,
        BlobGasUsed = (ulong)(blobs * 0x20000),
        ExcessBlobGas = 0x40000,
        ParentBeaconBlockRoot = TestItem.KeccakA,
        Transactions = BuildTransactions(count: txs, sizeEach: 600),
        Withdrawals = BuildWithdrawals(CapellaMaxWithdrawals),
    };

    private static Withdrawal[] BuildWithdrawals(int count)
    {
        Withdrawal[] withdrawals = new Withdrawal[count];
        for (int i = 0; i < count; i++)
        {
            withdrawals[i] = new Withdrawal
            {
                Index = (ulong)i,
                ValidatorIndex = (ulong)(i + 1),
                Address = TestItem.Addresses[i % TestItem.Addresses.Length],
                AmountInGwei = (ulong)((i + 1) * 1000),
            };
        }
        return withdrawals;
    }

    private static byte[][] BuildTransactions(int count, int sizeEach)
    {
        byte[][] txs = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            txs[i] = new byte[sizeEach];
            txs[i][0] = 0x02; // EIP-1559 type byte
        }
        return txs;
    }

    private static Hash256[] BuildBlobVersionedHashes(int count)
    {
        Hash256[] hashes = new Hash256[count];
        Span<byte> bytes = stackalloc byte[32];
        bytes[0] = 0x01; // BLOB_COMMITMENT_VERSION_KZG
        for (int i = 0; i < count; i++)
        {
            bytes[31] = (byte)(i + 1);
            hashes[i] = new Hash256(bytes);
        }
        return hashes;
    }

    private static byte[] EncodeSszBody(ExecutionPayloadV3 payload, Hash256[] blobs, Hash256 parentRoot)
    {
        NewPayloadV3RequestWire wire = new()
        {
            ExecutionPayload = new SszExecutionPayloadV3(payload),
            ExpectedBlobVersionedHashes = blobs,
            ParentBeaconBlockRoot = parentRoot,
        };

        byte[] dst = new byte[NewPayloadV3RequestWire.GetLength(wire)];
        NewPayloadV3RequestWire.Encode(dst, wire);
        return dst;
    }

    private static byte[] EncodeJsonBody(ExecutionPayloadV3 payload, Hash256[] blobs, Hash256 parentRoot)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter w = new(buffer);

        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WriteNumber("id", 1);
        w.WriteString("method", "engine_newPayloadV3");
        w.WriteStartArray("params");
        JsonSerializer.Serialize(w, payload, EthereumJsonSerializer.JsonOptions);
        JsonSerializer.Serialize(w, blobs, EthereumJsonSerializer.JsonOptions);
        JsonSerializer.Serialize(w, parentRoot, EthereumJsonSerializer.JsonOptions);
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    private static IEngineRpcModule BuildEngineStub()
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_newPayloadV1(default!).ReturnsForAnyArgs(ValidTask);
        engine.engine_newPayloadV2(default!).ReturnsForAnyArgs(ValidTask);
        engine.engine_newPayloadV3(default!, default!, default!).ReturnsForAnyArgs(ValidTask);
        engine.engine_newPayloadV4(default!, default!, default!, default!).ReturnsForAnyArgs(ValidTask);
        engine.engine_newPayloadV5(default!, default!, default!, default!).ReturnsForAnyArgs(ValidTask);
        return engine;
    }

    private static IHost BuildEngineHost(Action<IServiceCollection> configureServices, Action<IApplicationBuilder> configureApp)
    {
        IHost host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    JsonRpcUrl url = new("http", "localhost", EnginePort, RpcEndpoint.Http, isAuthenticated: true, ["engine"]);
                    services.AddSingleton<IJsonRpcUrlCollection>(new StubUrlCollection(EnginePort, url));
                    services.AddSingleton<IRpcAuthentication>(new StubRpcAuthentication());
                    services.AddSingleton<ILogManager>(LimboLogs.Instance);
                    configureServices(services);
                });
                web.Configure(app =>
                {
                    // TestServer has no real port; patch LocalPort so URL-collection lookup
                    // matches the registered authenticated engine URL.
                    app.Use(async (ctx, next) =>
                    {
                        ctx.Connection.LocalPort = EnginePort;
                        await next();
                    });
                    configureApp(app);
                });
            })
            .Build();

        host.Start();
        return host;
    }

    private static IHost BuildSszServer(IEngineRpcModule engine)
    {
        ISszEndpointHandler[] handlers =
        [
            new NewPayloadSszHandler<NewPayloadDescriptorV1, NewPayloadV1RequestWire>(engine),
            new NewPayloadSszHandler<NewPayloadDescriptorV2, NewPayloadV2RequestWire>(engine),
            new NewPayloadSszHandler<NewPayloadDescriptorV3, NewPayloadV3RequestWire>(engine),
            new NewPayloadSszHandler<NewPayloadDescriptorV4, NewPayloadV4RequestWire>(engine),
            new NewPayloadSszHandler<NewPayloadDescriptorV5, NewPayloadV5RequestWire>(engine),
        ];

        return BuildEngineHost(
            services =>
            {
                services.AddSingleton<IProcessExitSource>(new StubProcessExitSource());
                foreach (ISszEndpointHandler h in handlers)
                    services.AddSingleton<ISszEndpointHandler>(h);
            },
            app => app.UseMiddleware<SszMiddleware>());
    }

    private static IHost BuildJsonServer(IEngineRpcModule engine)
    {
        JsonRpcConfig config = new();
        IFileSystem fs = Substitute.For<IFileSystem>();

        RpcModuleProvider modules = new(fs, config, Serializer, LimboLogs.Instance);
        modules.Register(new SingletonModulePool<IEngineRpcModule>(engine, allowExclusive: true));

        JsonRpcService service = new(modules, LimboLogs.Instance, config);
        JsonRpcProcessor processor = new(service, config, fs, LimboLogs.Instance);

        return BuildEngineHost(
            _ => { },
            app => app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Method != "POST" ||
                    !(ctx.Request.ContentType?.Contains("application/json") ?? false))
                {
                    await next();
                    return;
                }

                IJsonRpcUrlCollection urls = ctx.RequestServices.GetRequiredService<IJsonRpcUrlCollection>();
                if (!urls.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl? url) || !url.IsAuthenticated)
                {
                    await next();
                    return;
                }

                IRpcAuthentication auth = ctx.RequestServices.GetRequiredService<IRpcAuthentication>();
                string? authHeader = ctx.Request.Headers.Authorization;
                if (authHeader is null || !await auth.Authenticate(authHeader))
                {
                    ctx.Response.StatusCode = 401;
                    return;
                }

                using JsonRpcContext rpcContext = JsonRpcContext.Http(url);
                await foreach (JsonRpcResult result in processor.ProcessAsync(ctx.Request.BodyReader, rpcContext))
                {
                    using (result)
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        await Serializer.SerializeAsync(ctx.Response.BodyWriter, result.Response!);
                        await ctx.Response.CompleteAsync();
                        break;
                    }
                }
            }));
    }

    private sealed class StubUrlCollection(int port, JsonRpcUrl url) : IJsonRpcUrlCollection
    {
        private readonly Dictionary<int, JsonRpcUrl> _inner = new() { [port] = url };

        public string[] Urls { get; } = [url.ToString()];
        public JsonRpcUrl this[int key] => _inner[key];
        public IEnumerable<int> Keys => _inner.Keys;
        public IEnumerable<JsonRpcUrl> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool ContainsKey(int key) => _inner.ContainsKey(key);

        public bool TryGetValue(int key, out JsonRpcUrl value)
        {
            if (_inner.TryGetValue(key, out JsonRpcUrl? found))
            {
                value = found;
                return true;
            }
            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<int, JsonRpcUrl>> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class StubRpcAuthentication : IRpcAuthentication
    {
        public Task<bool> Authenticate(string token) => Task.FromResult(true);
    }

    private sealed class StubProcessExitSource : IProcessExitSource
    {
        public CancellationToken Token => CancellationToken.None;
        public void Exit(int exitCode) { }
    }
}
