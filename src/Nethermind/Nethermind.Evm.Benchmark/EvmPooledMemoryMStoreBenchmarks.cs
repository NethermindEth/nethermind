// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "MSTORE")]
public class EvmPooledMemoryMStoreBenchmarks
{
    private const int FourKiB = 4 * 1024;
    private const int OneMiB = 1 << 20;
    private const int FourMiB = 4 << 20;

    [Params(
        EvmPooledMemory.WordSize,
        FourKiB,
        FourKiB + EvmPooledMemory.WordSize,
        2 * FourKiB,
        OneMiB,
        FourMiB + FourKiB)]
    // Covers word/page boundaries, large expansion, and sequential growth beyond pooled capacity.
    public int MemorySize { get; set; }

    [Benchmark]
    public ulong HighWaterJump()
    {
        EvmPooledMemory memory = default;
        try
        {
            EvmPooledMemoryBenchmarkHelper.MStore(ref memory, MemorySize - EvmPooledMemory.WordSize);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Benchmark]
    public ulong SequentialGrowth()
    {
        EvmPooledMemory memory = default;
        try
        {
            for (int offset = 0; offset < MemorySize; offset += EvmPooledMemory.WordSize)
            {
                EvmPooledMemoryBenchmarkHelper.MStore(ref memory, offset);
            }

            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }
}
