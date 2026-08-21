// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "MSTORE")]
public class EvmPooledMemoryMStoreBenchmarks
{
    private const int FourKiB = 4 * 1024;
    private const int OneMiB = 1 << 20;

    private static readonly byte[] Word = CreateWord();

    [Params(
        EvmPooledMemory.WordSize,
        FourKiB,
        FourKiB + EvmPooledMemory.WordSize,
        2 * FourKiB,
        OneMiB)]
    // Covers the prior zeroing scenarios: one word, one page, a page crossing,
    // multiple pages, and a large expansion. Keep these as unchanged controls.
    public int MemorySize { get; set; }

    [Benchmark]
    public ulong HighWaterJump()
    {
        EvmPooledMemory memory = default;
        try
        {
            MStore(ref memory, MemorySize - EvmPooledMemory.WordSize);
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
                MStore(ref memory, offset);
            }

            return memory.Size;
        }
        finally
        {
            memory.Dispose();
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
