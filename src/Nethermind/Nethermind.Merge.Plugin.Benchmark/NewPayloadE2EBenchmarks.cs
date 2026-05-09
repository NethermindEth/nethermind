// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Merge.Plugin.SszRest.Handlers;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.Benchmark;

/// <summary>
/// End-to-end benchmark that routes a <c>engine_newPayloadV3</c> with the maximum number of
/// blobs (6 per EIP-4844) through the full Kestrel middleware stack and compares:
/// <list type="bullet">
///   <item><description>
///     <b>SSZ</b> – binary <c>application/octet-stream</c> POST to
///     <c>/engine/v3/payloads</c> via <see cref="SszMiddleware"/>.
///   </description></item>
///   <item><description>
///     <b>JSON</b> – <c>application/json</c> POST of the standard JSON-RPC envelope
///     (<c>engine_newPayloadV3</c>) through the Nethermind JSON-RPC fast-lane middleware.
///   </description></item>
/// </list>
/// Both paths call the same stub <see cref="IEngineRpcModule"/> that immediately returns
/// <c>VALID</c> so the benchmark isolates serialization / middleware overhead rather than
/// block-processing time.
/// </summary>
[MemoryDiagnoser]
public class NewPayloadE2EBenchmarks : IDisposable
{
    private const int MaxBlobs = 6;

    private const string BearerToken =
        "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9" +
        ".eyJpYXQiOjE3MDAwMDAwMDB9" +
        ".stubTokenForBenchmarkingOnly";

    private static readonly Hash256 KeccakA = Keccak.Compute("A");
    private static readonly Hash256 KeccakB = Keccak.Compute("B");
    private static readonly Hash256 KeccakC = Keccak.Compute("C");
    private static readonly Hash256 KeccakD = Keccak.Compute("D");
    private static readonly Hash256 KeccakE = Keccak.Compute("E");
    private static readonly Address AddressA = new("0xb7705ae4c6f81b66cdb323c65f4e8133690fc099");
    private static readonly Address AddressB = new("0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358");

    private IHost _sszHost = null!;
    private IHost _jsonHost = null!;
    private TestServer _sszServer = null!;
    private TestServer _jsonServer = null!;
    private HttpClient _sszClient = null!;
    private HttpClient _jsonClient = null!;
    private byte[] _sszBody = null!;
    private byte[] _jsonBody = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        ExecutionPayloadV3 payload = BuildMaxBlobPayload();
        Hash256[] blobVersionedHashes = BuildBlobVersionedHashes(MaxBlobs);
        Hash256 parentBeaconBlockRoot = KeccakA;

        _sszBody = EncodeSszBody(payload, blobVersionedHashes, parentBeaconBlockRoot);
        _jsonBody = EncodeJsonBody(payload, blobVersionedHashes, parentBeaconBlockRoot);

        StubEngineModule stub = new();
        (_sszHost, _sszServer) = BuildSszServer(stub);
        (_jsonHost, _jsonServer) = BuildJsonServer(stub);

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

    [Benchmark(Description = "SSZ  NewPayloadV3 max-blobs (full Kestrel round-trip)")]
    public async Task<HttpResponseMessage> SszNewPayloadV3_MaxBlobs()
    {
        using ByteArrayContent content = new(_sszBody);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using HttpRequestMessage req = new(HttpMethod.Post, "/engine/v3/payloads");
        req.Content = content;
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(BearerToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        HttpResponseMessage response = await _sszClient.SendAsync(req);
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "JSON NewPayloadV3 max-blobs (full Kestrel round-trip)", Baseline = true)]
    public async Task<HttpResponseMessage> JsonNewPayloadV3_MaxBlobs()
    {
        using ByteArrayContent content = new(_jsonBody);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpRequestMessage req = new(HttpMethod.Post, "/");
        req.Content = content;
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(BearerToken);

        HttpResponseMessage response = await _jsonClient.SendAsync(req);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static ExecutionPayloadV3 BuildMaxBlobPayload()
    {
        ExecutionPayloadV3 ep = new()
        {
            ParentHash = KeccakA,
            FeeRecipient = AddressA,
            StateRoot = KeccakB,
            ReceiptsRoot = KeccakC,
            LogsBloom = Bloom.Empty,
            PrevRandao = KeccakD,
            BlockNumber = 20_000_000,
            GasLimit = 30_000_000,
            GasUsed = 15_000_000,
            Timestamp = 1_700_100_000,
            ExtraData = new byte[32],
            BaseFeePerGas = 10_000_000_000,
            BlockHash = KeccakE,
            BlobGasUsed = (ulong)(MaxBlobs * 0x20000),
            ExcessBlobGas = 0x40000,
            ParentBeaconBlockRoot = KeccakA,
        };

        ep.Transactions = BuildTransactions(count: 50, sizeEach: 300);
        ep.Withdrawals =
        [
            new Withdrawal { Index = 0, ValidatorIndex = 1, Address = AddressA, AmountInGwei = 1000 },
            new Withdrawal { Index = 1, ValidatorIndex = 2, Address = AddressB, AmountInGwei = 2000 },
        ];

        return ep;
    }

    private static byte[][] BuildTransactions(int count, int sizeEach)
    {
        byte[][] txs = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            byte[] tx = new byte[sizeEach];
            tx[0] = 0x02; // EIP-1559 type byte
            new Random(i).NextBytes(tx.AsSpan(1));
            txs[i] = tx;
        }
        return txs;
    }

    private static Hash256[] BuildBlobVersionedHashes(int count)
    {
        Hash256[] hashes = new Hash256[count];
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = new byte[32];
            bytes[0] = 0x01; // BLOB_COMMITMENT_VERSION_KZG
            bytes[31] = (byte)(i + 1);
            hashes[i] = new Hash256(bytes);
        }
        return hashes;
    }

    private static byte[] EncodeSszBody(
        ExecutionPayloadV3 payload,
        Hash256[] blobVersionedHashes,
        Hash256 parentBeaconBlockRoot)
    {
        NewPayloadV3RequestWire wire = new()
        {
            ExecutionPayload = new SszExecutionPayloadV3(payload),
            ExpectedBlobVersionedHashes = blobVersionedHashes,
            ParentBeaconBlockRoot = parentBeaconBlockRoot,
        };

        ArrayBufferWriter<byte> writer = new();
        int length = NewPayloadV3RequestWire.GetLength(wire);
        Span<byte> dst = writer.GetSpan(length)[..length];
        NewPayloadV3RequestWire.Encode(dst, wire);
        writer.Advance(length);
        return writer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeJsonBody(
        ExecutionPayloadV3 payload,
        Hash256[] blobVersionedHashes,
        Hash256 parentBeaconBlockRoot)
    {
        EthereumJsonSerializer serializer = new();

        using MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);

        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WriteNumber("id", 1);
        writer.WriteString("method", "engine_newPayloadV3");
        writer.WriteStartArray("params");

        string payloadJson = serializer.Serialize(payload);
        using JsonDocument payloadDoc = JsonDocument.Parse(payloadJson);
        payloadDoc.RootElement.WriteTo(writer);

        writer.WriteStartArray();
        foreach (Hash256 h in blobVersionedHashes)
            writer.WriteStringValue(h.ToString());
        writer.WriteEndArray();

        writer.WriteStringValue(parentBeaconBlockRoot.ToString());

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return ms.ToArray();
    }

    private static (IHost Host, TestServer Server) BuildSszServer(StubEngineModule stub)
    {
        const int enginePort = 8551;

        JsonRpcUrl engineUrl = new("http", "localhost", enginePort, RpcEndpoint.Http, isAuthenticated: true, ["engine"]);
        StubUrlCollection urlCollection = new(enginePort, engineUrl);
        StubRpcAuthentication auth = new();
        StubProcessExitSource processExit = new();

        ISszEndpointHandler[] handlers =
        [
            new NewPayloadSszHandler<NewPayloadDescriptorV1, NewPayloadV1RequestWire>(stub),
            new NewPayloadSszHandler<NewPayloadDescriptorV2, NewPayloadV2RequestWire>(stub),
            new NewPayloadSszHandler<NewPayloadDescriptorV3, NewPayloadV3RequestWire>(stub),
            new NewPayloadSszHandler<NewPayloadDescriptorV4, NewPayloadV4RequestWire>(stub),
            new NewPayloadSszHandler<NewPayloadDescriptorV5, NewPayloadV5RequestWire>(stub),
        ];

        IHost sszHost = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IJsonRpcUrlCollection>(urlCollection);
                    services.AddSingleton<IRpcAuthentication>(auth);
                    services.AddSingleton<IProcessExitSource>(processExit);
                    services.AddSingleton<ILogManager>(LimboLogs.Instance);
                    foreach (ISszEndpointHandler h in handlers)
                        services.AddSingleton<ISszEndpointHandler>(h);
                });
                web.Configure(app =>
                {
                    // TestServer has no real port; patch LocalPort so SszMiddleware's
                    // URL-collection lookup finds the authenticated engine URL.
                    app.Use(async (ctx, next) =>
                    {
                        ctx.Connection.LocalPort = enginePort;
                        await next();
                    });
                    app.UseMiddleware<SszMiddleware>();
                });
            })
            .Build();

        sszHost.Start();
        return (sszHost, sszHost.GetTestServer());
    }

    private static (IHost Host, TestServer Server) BuildJsonServer(StubEngineModule stub)
    {
        const int enginePort = 8551;

        JsonRpcUrl engineUrl = new("http", "localhost", enginePort, RpcEndpoint.Http, isAuthenticated: true, ["engine"]);
        StubUrlCollection urlCollection = new(enginePort, engineUrl);
        StubRpcAuthentication auth = new();

        IHost jsonHost = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.Configure(app =>
                {
                    app.Use(async (ctx, next) =>
                    {
                        ctx.Connection.LocalPort = enginePort;
                        await next();
                    });

                    app.Use(async (ctx, next) =>
                    {
                        if (ctx.Request.Method != "POST" ||
                            !(ctx.Request.ContentType?.Contains("application/json") ?? false) ||
                            !urlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl? jsonRpcUrl) ||
                            !jsonRpcUrl.IsAuthenticated)
                        {
                            await next();
                            return;
                        }

                        string? authHeader = ctx.Request.Headers.Authorization;
                        if (authHeader is null || !await auth.Authenticate(authHeader))
                        {
                            ctx.Response.StatusCode = 401;
                            return;
                        }

                        await stub.HandleJsonRpcRequestAsync(ctx);
                    });

                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = 404;
                        return Task.CompletedTask;
                    });
                });
            })
            .Build();

        jsonHost.Start();
        return (jsonHost, jsonHost.GetTestServer());
    }

    private sealed class StubEngineModule : IEngineRpcModule
    {
        private static readonly ResultWrapper<PayloadStatusV1> ValidResult =
            ResultWrapper<PayloadStatusV1>.Success(
                new PayloadStatusV1 { Status = PayloadStatus.Valid });

        private static readonly Task<ResultWrapper<PayloadStatusV1>> ValidTask =
            Task.FromResult(ValidResult);

        private static readonly byte[] JsonValidResponse = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"result":{"status":"VALID","latestValidHash":null,"validationError":null}}""");

        private static readonly EthereumJsonSerializer JsonSerializer = new();

        public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayload payload)
            => ValidTask;

        public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(ExecutionPayload payload)
            => ValidTask;

        public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(
            ExecutionPayloadV3 payload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot)
            => ValidTask;

        public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV4(
            ExecutionPayloadV3 payload, byte[]?[] blobVersionedHashes,
            Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
            => ValidTask;

        public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(
            ExecutionPayloadV4 payload, byte[]?[] blobVersionedHashes,
            Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
            => ValidTask;

        public ResultWrapper<IReadOnlyList<string>> engine_exchangeCapabilities(IEnumerable<string> methods)
            => throw new NotImplementedException();

        public ResultWrapper<ClientVersionV1[]> engine_getClientVersionV1(ClientVersionV1 clientVersion)
            => throw new NotImplementedException();

        public ResultWrapper<TransitionConfigurationV1> engine_exchangeTransitionConfigurationV1(
            TransitionConfigurationV1 transitionConfiguration)
            => throw new NotImplementedException();

        public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(
            ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes)
            => throw new NotImplementedException();

        public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(
            ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes)
            => throw new NotImplementedException();

        public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV3(
            ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes)
            => throw new NotImplementedException();

        public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV4(
            ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes)
            => throw new NotImplementedException();

        public Task<ResultWrapper<GetPayloadV3Result?>> engine_getPayloadV3(byte[] payloadId)
            => throw new NotImplementedException();

        public Task<ResultWrapper<GetPayloadV4Result?>> engine_getPayloadV4(byte[] payloadId)
            => throw new NotImplementedException();

        public Task<ResultWrapper<GetPayloadV5Result?>> engine_getPayloadV5(byte[] payloadId)
            => throw new NotImplementedException();

        public Task<ResultWrapper<GetPayloadV6Result?>> engine_getPayloadV6(byte[] payloadId)
            => throw new NotImplementedException();

        public Task<ResultWrapper<IReadOnlyList<BlobAndProofV1?>>> engine_getBlobsV1(byte[][] blobVersionedHashes)
            => throw new NotImplementedException();

        public Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> engine_getBlobsV2(byte[][] blobVersionedHashes)
            => throw new NotImplementedException();

        public Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> engine_getBlobsV3(byte[][] blobVersionedHashes)
            => throw new NotImplementedException();

        public Task<ResultWrapper<ExecutionPayload?>> engine_getPayloadV1(byte[] payloadId)
            => throw new NotImplementedException();

        public Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId)
            => throw new NotImplementedException();

        public ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>> engine_getPayloadBodiesByHashV1(
            IReadOnlyList<Hash256> blockHashes)
            => throw new NotImplementedException();

        public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> engine_getPayloadBodiesByRangeV1(
            long start, long count)
            => throw new NotImplementedException();

        public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByHashV2(
            IReadOnlyList<Hash256> blockHashes)
            => throw new NotImplementedException();

        public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByRangeV2(
            long start, long count)
            => throw new NotImplementedException();

        public async Task HandleJsonRpcRequestAsync(HttpContext ctx)
        {
            using StreamReader reader = new(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            string body = await reader.ReadToEndAsync(ctx.RequestAborted);

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            string method = root.GetProperty("method").GetString() ?? string.Empty;
            JsonElement paramsEl = root.GetProperty("params");

            if (method == "engine_newPayloadV3")
            {
                string payloadJson = paramsEl[0].GetRawText();
                _ = JsonSerializer.Deserialize<ExecutionPayloadV3>(payloadJson);

                foreach (JsonElement _ in paramsEl[1].EnumerateArray()) { }
                _ = paramsEl[2].GetString();
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength = JsonValidResponse.Length;
            await ctx.Response.Body.WriteAsync(JsonValidResponse, ctx.RequestAborted);
        }
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
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
