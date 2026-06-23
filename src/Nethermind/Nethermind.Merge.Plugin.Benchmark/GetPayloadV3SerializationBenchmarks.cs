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
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Merge.Plugin.SszRest.Handlers;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Ssz;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Benchmark;

/// <summary>
/// Compares SSZ-REST and JSON-RPC for engine_getPayloadV3 across decode, encode, and full
/// Kestrel round-trip. The response carries the full BlobsBundle (Blobs × 131,072 bytes of
/// blob data + 48-byte commitments + 48-byte proofs) plus the execution payload — the
/// largest hot RPC on the engine API and where the SSZ vs JSON delta is most pronounced
/// (JSON hex-encodes blob bytes, doubling the wire size).
/// </summary>
[MemoryDiagnoser]
public class GetPayloadV3SerializationBenchmarks : IDisposable
{
    private const int BlobBytes = (int)Eip4844Constants.GasPerBlob;  // 131,072 — 4096 BLS field elements × 32 B
    private const int CommitmentBytes = 48;    // KZG commitment (BLS12-381 G1 compressed)
    private const int ProofBytes = 48;         // KZG proof

    // Heavy-block baseline: 250 × ~600 B RLP ≈ 150 KB of tx data — typical mainnet block.
    private const int Txs = 250;
    private const int TxDataBytes = 460;       // ~600 B RLP-encoded after envelope + signature

    private const string PayloadIdHex = "0x0001020304050607";

    [Params(0, 1, 3, 6, 12, 24, 36, 72)]
    public int Blobs;

    private GetPayloadV3Result _result = null!;
    private byte[] _sszEncoded = null!;
    private byte[] _jsonEncoded = null!;
    private byte[] _jsonRequestBody = null!;

    private IHost _sszHost = null!;
    private IHost _jsonHost = null!;
    private TestServer _sszServer = null!;
    private TestServer _jsonServer = null!;
    private HttpClient _sszClient = null!;
    private HttpClient _jsonClient = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _result = BuildResult(Blobs);
        _sszEncoded = EncodeSsz(_result);
        _jsonEncoded = JsonSerializer.SerializeToUtf8Bytes(_result, EthereumJsonSerializer.JsonOptions);
        _jsonRequestBody = BuildJsonRpcRequest(PayloadIdHex);

        IEngineRpcModule engine = BuildEngineStub(_result);
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

    [Benchmark(Description = "SSZ  decode GetPayloadV3 wire")]
    public GetPayloadResponseV3Wire DeserializeSsz()
    {
        GetPayloadResponseV3Wire.Decode(new ReadOnlySequence<byte>(_sszEncoded), out GetPayloadResponseV3Wire wire);
        return wire;
    }

    /// <summary>
    /// GetPayloadV3Result has no parameterless ctor and isn't STJ-constructible — the EL emits
    /// it but no Nethermind type deserializes it back. Production CL clients parse the JSON
    /// into their own DOM. JsonDocument.Parse models that lexer cost (no hex→byte conversion);
    /// the SSZ counterpart similarly produces a wire struct with byte-array views, no copies.
    /// </summary>
    [Benchmark(Description = "JSON parse GetPayloadV3 (JsonDocument)", Baseline = true)]
    public int DeserializeJson()
    {
        using JsonDocument doc = JsonDocument.Parse(_jsonEncoded);
        int count = 0;
        foreach (JsonProperty _ in doc.RootElement.EnumerateObject()) count++;
        return count;
    }

    [Benchmark(Description = "SSZ  encode GetPayloadV3 result")]
    public int SerializeSsz()
    {
        ArrayBufferWriter<byte> writer = new(_sszEncoded.Length);
        return SszCodec.EncodeGetPayloadV3Response(_result, writer);
    }

    [Benchmark(Description = "JSON encode GetPayloadV3Result")]
    public byte[] SerializeJson() =>
        JsonSerializer.SerializeToUtf8Bytes(_result, EthereumJsonSerializer.JsonOptions);

    [Benchmark(Description = "SSZ  GetPayloadV3 (full Kestrel round-trip)")]
    public async Task<int> SszRoundTrip()
    {
        using HttpRequestMessage req = new(HttpMethod.Get, $"/engine/v3/payloads/{PayloadIdHex}");
        req.Headers.Authorization = EngineBenchmarkHost.Authorization;
        req.Headers.Accept.Add(EngineBenchmarkHost.OctetAccept);

        using HttpResponseMessage response = await _sszClient.SendAsync(req);
        response.EnsureSuccessStatusCode();
        // Drain so the response body cost is included in the measurement.
        await response.Content.CopyToAsync(System.IO.Stream.Null);
        return (int)response.StatusCode;
    }

    [Benchmark(Description = "JSON GetPayloadV3 (full Kestrel round-trip)")]
    public async Task<int> JsonRoundTrip()
    {
        using ByteArrayContent content = new(_jsonRequestBody);
        content.Headers.ContentType = EngineBenchmarkHost.ApplicationJson;

        using HttpRequestMessage req = new(HttpMethod.Post, "/") { Content = content };
        req.Headers.Authorization = EngineBenchmarkHost.Authorization;

        using HttpResponseMessage response = await _jsonClient.SendAsync(req);
        response.EnsureSuccessStatusCode();
        await response.Content.CopyToAsync(System.IO.Stream.Null);
        return (int)response.StatusCode;
    }

    private static GetPayloadV3Result BuildResult(int blobs)
    {
        Block block = BuildBlock(Txs);
        BlobsBundleV1 bundle = BuildBlobsBundle(blobs);
        return new GetPayloadV3Result(block, blockFees: UInt256.One, bundle, shouldOverrideBuilder: false);
    }

    private static Block BuildBlock(int txCount)
    {
        Transaction[] txs = new Transaction[txCount];
        for (int i = 0; i < txCount; i++)
        {
            txs[i] = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(1)
                .WithNonce((UInt256)i)
                .WithGasLimit(21_000)
                .WithMaxFeePerGas(20_000_000_000)
                .WithMaxPriorityFeePerGas(1_000_000_000)
                .WithTo(TestItem.AddressB)
                .WithValue(1)
                .WithData(new byte[TxDataBytes])
                .Signed(TestItem.PrivateKeyA)
                .TestObject;
        }

        return Build.A.Block
            .WithNumber(20_000_000)
            .WithGasLimit(30_000_000)
            .WithTimestamp(1_700_100_000)
            .WithBaseFeePerGas(10_000_000_000)
            .WithExcessBlobGas(0x40000)
            .WithParentBeaconBlockRoot(TestItem.KeccakA)
            .WithTransactions(txs)
            .WithWithdrawals(EngineBenchmarkHost.BuildWithdrawals(EngineBenchmarkHost.CapellaMaxWithdrawals))
            .TestObject;
    }

    private static BlobsBundleV1 BuildBlobsBundle(int blobs)
    {
        byte[][] commits = new byte[blobs][];
        byte[][] blobBytes = new byte[blobs][];
        byte[][] proofs = new byte[blobs][];
        for (int i = 0; i < blobs; i++)
        {
            commits[i] = new byte[CommitmentBytes];
            commits[i][0] = (byte)(i + 1);
            blobBytes[i] = new byte[BlobBytes];
            blobBytes[i][0] = (byte)(i + 1);
            proofs[i] = new byte[ProofBytes];
            proofs[i][0] = (byte)(i + 1);
        }
        return new BlobsBundleV1(commits, blobBytes, proofs);
    }

    private static byte[] EncodeSsz(GetPayloadV3Result result)
    {
        ArrayBufferWriter<byte> writer = new();
        SszCodec.EncodeGetPayloadV3Response(result, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] BuildJsonRpcRequest(string payloadIdHex)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter w = new(buffer);

        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WriteNumber("id", 1);
        w.WriteString("method", "engine_getPayloadV3");
        w.WriteStartArray("params");
        w.WriteStringValue(payloadIdHex);
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    private static IEngineRpcModule BuildEngineStub(GetPayloadV3Result result)
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        Task<ResultWrapper<GetPayloadV3Result?>> task =
            Task.FromResult(ResultWrapper<GetPayloadV3Result?>.Success(result));
        engine.engine_getPayloadV3(default!).ReturnsForAnyArgs(task);
        return engine;
    }

    private static IHost BuildSszServer(IEngineRpcModule engine)
    {
        ISszEndpointHandler[] handlers =
        [
            new GetPayloadSszHandler<GetPayloadDescriptorV1, GetPayloadV2Result>(engine),
            new GetPayloadSszHandler<GetPayloadDescriptorV2, GetPayloadV2Result>(engine),
            new GetPayloadSszHandler<GetPayloadDescriptorV3, GetPayloadV3Result>(engine),
            new GetPayloadSszHandler<GetPayloadDescriptorV4, GetPayloadV4Result>(engine),
            new GetPayloadSszHandler<GetPayloadDescriptorV5, GetPayloadV5Result>(engine),
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
