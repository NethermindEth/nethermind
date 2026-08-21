// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "FreshAllocation")]
public class EvmPooledMemoryPrivateCacheBurstBenchmarks
{
    private const int CacheSlots = 16;
    private const int MemorySize = 64 * 1024;
    private static readonly byte[] Word = CreateWord();

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
                MStore(ref _frames[i], MemorySize - EvmPooledMemory.WordSize);
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

    private static void MStore(ref EvmPooledMemory memory, int offset)
    {
        UInt256 destination = (UInt256)offset;
        memory.CalculateMemoryCost(in destination, EvmPooledMemory.WordSize, out _);
        memory.StoreWordAfterGas(in destination, Word);
    }

    private static byte[] CreateWord()
    {
        byte[] word = new byte[EvmPooledMemory.WordSize];
        for (int i = 0; i < word.Length; i++)
        {
            word[i] = (byte)(i * 37 + 0x41);
        }

        return word;
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "FreshAllocation")]
public class EvmPooledMemoryNonPooledAllocationBenchmarks
{
    private const int FourMiB = 4 << 20;
    private static readonly byte[] Word = CreateWord();

    [Params(FourMiB + EvmPooledMemory.WordSize, 8 << 20)]
    public int MemorySize { get; set; }

    [Benchmark]
    public ulong HighWaterMStore()
    {
        EvmPooledMemory memory = default;
        try
        {
            UInt256 destination = (UInt256)(MemorySize - EvmPooledMemory.WordSize);
            memory.CalculateMemoryCost(in destination, EvmPooledMemory.WordSize, out _);
            memory.StoreWordAfterGas(in destination, Word);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    private static byte[] CreateWord()
    {
        byte[] word = new byte[EvmPooledMemory.WordSize];
        for (int i = 0; i < word.Length; i++)
        {
            word[i] = (byte)(i * 37 + 0x41);
        }

        return word;
    }
}
