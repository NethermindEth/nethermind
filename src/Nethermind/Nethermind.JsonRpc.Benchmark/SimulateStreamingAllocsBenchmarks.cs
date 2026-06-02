// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Specs;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Serialization.Json;
using Nethermind.Specs;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// Allocation-oriented counterpart to <see cref="SimulateStreamingBenchmarks"/>. Compares
/// peak heap retention of the buffered path (whole list of <see cref="SimulateBlockResult{T}"/>
/// alive simultaneously) against the streaming path (one block in flight, GC-eligible the
/// moment its JSON has been written).
/// </summary>
[MemoryDiagnoser]
public class SimulateStreamingAllocsBenchmarks
{
    [Params(8, 32, 128)]
    public int BlockCount { get; set; }

    [Params(10, 50)]
    public int CallsPerBlock { get; set; }

    private ISpecProvider _specProvider = null!;

    [GlobalSetup]
    public void Setup() => _specProvider = MainnetSpecProvider.Instance;

    [Benchmark(Baseline = true, Description = "Buffered: build the full list, then serialize. Peak heap = N × SimulateBlockResult + their inner Calls/Logs.")]
    public int Buffered_PeakRetention()
    {
        // Mirrors the current eth_simulateV1 code path: accumulate the cross-block list, then
        // serialize once. The list reference survives until the JSON envelope is closed.
        List<SimulateBlockResult<SimulateCallResult>> blocks = SimulateStreamingBenchmarks.BuildSyntheticBlocks(BlockCount, CallsPerBlock, _specProvider);

        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink, new JsonWriterOptions { SkipValidation = true });
        JsonSerializer.Serialize(writer, blocks, EthereumJsonSerializer.JsonOptions);
        return sink.WrittenCount;
    }

    [Benchmark(Description = "Streaming: build one block, serialize it, drop the reference, repeat. Peak heap = 1 × SimulateBlockResult.")]
    public int Streaming_PeakRetention()
    {
        // Mirrors the streaming code path: build the next block on demand, serialize it,
        // null out the local before the next iteration runs.
        ArrayBufferWriter<byte> sink = new();
        using Utf8JsonWriter writer = new(sink, new JsonWriterOptions { SkipValidation = true });
        writer.WriteStartArray();
        for (int i = 0; i < BlockCount; i++)
        {
            SimulateBlockResult<SimulateCallResult> oneBlock = SimulateStreamingBenchmarks.BuildSyntheticBlocks(1, CallsPerBlock, _specProvider)[0];
            JsonSerializer.Serialize(writer, oneBlock, EthereumJsonSerializer.JsonOptions);
            // Local goes out of scope at the end of the iteration; the next iteration's allocation
            // can reuse the gen0 slot vacated by the previous one. This is exactly what the engine
            // achieves in flight: peak retention is bounded by one block at a time.
        }
        writer.WriteEndArray();
        return sink.WrittenCount;
    }
}
