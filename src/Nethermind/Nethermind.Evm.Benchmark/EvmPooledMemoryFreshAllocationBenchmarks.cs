// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "FreshAllocation")]
public class EvmPooledMemoryPrivateCacheBurstBenchmarks
{
    private const int CacheSlots = 16;
    private const int MemorySize = 64 * 1024;
    private EvmPooledMemory[] _frames = null!;

    [Params(CacheSlots / 2, CacheSlots * 2)]
    public int FrameCount { get; set; }

    [GlobalSetup]
    public void Setup() => _frames = new EvmPooledMemory[FrameCount];

    [Benchmark]
    public ulong HighWaterMStoreBurst()
    {
        ulong totalSize = 0;
        try
        {
            for (int i = 0; i < _frames.Length; i++)
            {
                EvmPooledMemoryBenchmarkHelper.MStore(ref _frames[i], MemorySize - EvmPooledMemory.WordSize);
                totalSize += _frames[i].Size;
            }

            return totalSize;
        }
        finally
        {
            for (int i = 0; i < _frames.Length; i++)
            {
                _frames[i].Dispose();
            }
        }
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "FreshAllocation")]
public class EvmPooledMemoryNonPooledAllocationBenchmarks
{
    private const int FourMiB = 4 << 20;
    [Params(FourMiB + EvmPooledMemory.WordSize, 8 << 20)]
    public int MemorySize { get; set; }

    [Benchmark]
    public ulong HighWaterMStore()
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
}
