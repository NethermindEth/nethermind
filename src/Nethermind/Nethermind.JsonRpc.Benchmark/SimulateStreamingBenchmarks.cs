// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// Throughput-oriented benchmark for the simulate streaming serializer. Drives only the
/// JSON-emit half of <c>eth_simulateV1</c> so the result is not muddied by EVM cost.
/// </summary>
[MemoryDiagnoser]
public class SimulateStreamingBenchmarks
{
    [Params(1, 8, 64)]
    public int BlockCount { get; set; }

    [Params(1, 10, 50)]
    public int CallsPerBlock { get; set; }

    private List<SimulateBlockResult<SimulateCallResult>> _blocks = null!;
    private ISpecProvider _specProvider = null!;

    [GlobalSetup]
    public void Setup()
    {
        _specProvider = MainnetSpecProvider.Instance;
        _blocks = BuildSyntheticBlocks(BlockCount, CallsPerBlock, _specProvider);
    }

    [Benchmark(Baseline = true, Description = "Buffered: serialize the full IReadOnlyList<SimulateBlockResult> in one Serialize call")]
    public int Buffered_SerializeAll()
    {
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink, new JsonWriterOptions { SkipValidation = true });
        JsonSerializer.Serialize(writer, _blocks, EthereumJsonSerializer.JsonOptions);
        return sink.WrittenCount;
    }

    [Benchmark(Description = "Streaming: write outer [, then serialize each block inline (matches the per-block emit path)")]
    public int Streaming_SerializePerBlock()
    {
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink, new JsonWriterOptions { SkipValidation = true });
        writer.WriteStartArray();
        for (int i = 0; i < _blocks.Count; i++)
        {
            JsonSerializer.Serialize(writer, _blocks[i], EthereumJsonSerializer.JsonOptions);
        }
        writer.WriteEndArray();
        return sink.WrittenCount;
    }

    internal static List<SimulateBlockResult<SimulateCallResult>> BuildSyntheticBlocks(int blockCount, int callsPerBlock, ISpecProvider specProvider)
    {
        List<SimulateBlockResult<SimulateCallResult>> results = new(blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            Block sourceBlock = Build.A.Block
                .WithNumber(i + 1)
                .WithDifficulty(1)
                .WithTotalDifficulty(new UInt256((ulong)(i + 1)))
                .WithGasLimit(30_000_000)
                .WithGasUsed(21_000)
                .WithBaseFeePerGas(0)
                .WithTimestamp((ulong)(1_700_000_000 + i * 12))
                .WithParentHash(Keccak.Zero)
                .WithBeneficiary(TestItem.AddressA)
                .TestObject;
            sourceBlock.Header.Hash = sourceBlock.Header.CalculateHash();

            SimulateCallResult[] calls = new SimulateCallResult[callsPerBlock];
            for (int c = 0; c < callsPerBlock; c++)
            {
                calls[c] = new SimulateCallResult
                {
                    Status = 1,
                    GasUsed = 21_000,
                    MaxUsedGas = 21_000,
                    ReturnData = [0x01, 0x02, 0x03, 0x04],
                    Logs = BuildLogs(sourceBlock, c, logCount: 4),
                };
            }

            SimulateBlockResult<SimulateCallResult> blockResult = new(sourceBlock, includeFullTransactionData: false, specProvider)
            {
                Calls = calls,
            };
            results.Add(blockResult);
        }
        return results;
    }

    private static List<Log> BuildLogs(Block source, int callIndex, int logCount)
    {
        List<Log> logs = new(logCount);
        for (int i = 0; i < logCount; i++)
        {
            logs.Add(new Log
            {
                Address = TestItem.AddressB,
                Topics = [Keccak.Compute("Transfer(address,address,uint256)"), Keccak.Zero, Keccak.Zero],
                Data = [.. new byte[32]],
                BlockNumber = (ulong)source.Number,
                TransactionHash = Keccak.Zero,
                TransactionIndex = (ulong)callIndex,
                BlockHash = source.Hash!,
                BlockTimestamp = (ulong)source.Timestamp,
                LogIndex = (ulong)(callIndex * logCount + i),
            });
        }
        return logs;
    }
}
