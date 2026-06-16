// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
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
    private static readonly ResultWrapper<PayloadStatusV1> ValidResult =
        ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid });
    private static readonly Task<ResultWrapper<PayloadStatusV1>> ValidTask = Task.FromResult(ValidResult);

    [Params(0, 1, 3, 6, 12, 24, 36, 72)]
    public int Blobs;

    // Heavy-block baseline: 250 × 600 B ≈ 150 KB of tx data — typical mainnet block.
    // Bump for worst-case stress (~2500 × 600 B ≈ 1.5 MB).
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
        ExecutionPayloadV3 payload = BuildPayload(Blobs, Txs);
        Hash256[] blobHashes = BuildBlobVersionedHashes(Blobs);
        Hash256 parentRoot = TestItem.KeccakA;

        _sszBody = EncodeSszBody(payload, parentRoot);
        _jsonBody = EncodeJsonBody(payload, blobHashes, parentRoot);
        _jsonPayloadOnly = JsonSerializer.SerializeToUtf8Bytes(payload, EthereumJsonSerializer.JsonOptions);

        IEngineRpcModule engine = BuildEngineStub();
        _sszHost = BuildSszServer(engine);
        _jsonHost = EngineBenchmarkHost.BuildJsonServer(engine);
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
        EngineBenchmarkHost.Serializer.Deserialize<ExecutionPayloadV3>(_jsonPayloadOnly)!;

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
        content.Headers.ContentType = EngineBenchmarkHost.OctetStream;

        using HttpRequestMessage req = new(HttpMethod.Post, "/engine/v3/payloads") { Content = content };
        req.Headers.Authorization = EngineBenchmarkHost.Authorization;
        req.Headers.Accept.Add(EngineBenchmarkHost.OctetAccept);

        using HttpResponseMessage response = await _sszClient.SendAsync(req);
        response.EnsureSuccessStatusCode();
        return (int)response.StatusCode;
    }

    [Benchmark(Description = "JSON NewPayloadV3 (full Kestrel round-trip)")]
    public async Task<int> JsonNewPayloadV3_RoundTrip()
    {
        using ByteArrayContent content = new(_jsonBody);
        content.Headers.ContentType = EngineBenchmarkHost.ApplicationJson;

        using HttpRequestMessage req = new(HttpMethod.Post, "/") { Content = content };
        req.Headers.Authorization = EngineBenchmarkHost.Authorization;

        using HttpResponseMessage response = await _jsonClient.SendAsync(req);
        response.EnsureSuccessStatusCode();
        return (int)response.StatusCode;
    }

    private static ExecutionPayloadV3 BuildPayload(int blobs, int txs) => new()
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
        BlobGasUsed = (ulong)blobs * Eip4844Constants.GasPerBlob,
        ExcessBlobGas = 0x40000,
        ParentBeaconBlockRoot = TestItem.KeccakA,
        Transactions = BuildTransactions(count: txs, sizeEach: 600),
        Withdrawals = EngineBenchmarkHost.BuildWithdrawals(EngineBenchmarkHost.CapellaMaxWithdrawals),
    };

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
        Span<byte> bytes = stackalloc byte[Eip4844Constants.BytesPerBlobVersionedHash];
        bytes[0] = KzgPolynomialCommitments.KzgBlobHashVersionV1;
        for (int i = 0; i < count; i++)
        {
            bytes[^1] = (byte)(i + 1);
            hashes[i] = new Hash256(bytes);
        }
        return hashes;
    }

    private static byte[] EncodeSszBody(ExecutionPayloadV3 payload, Hash256 parentRoot)
    {
        NewPayloadV3RequestWire wire = new()
        {
            ExecutionPayload = new SszExecutionPayloadV3(payload),
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

        return EngineBenchmarkHost.Build(
            services =>
            {
                foreach (ISszEndpointHandler h in handlers)
                    services.AddSingleton<ISszEndpointHandler>(h);
            },
            app => app.UseMiddleware<SszMiddleware>());
    }
}
