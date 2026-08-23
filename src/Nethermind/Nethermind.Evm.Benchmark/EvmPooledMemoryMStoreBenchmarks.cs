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
    private readonly EvmPooledMemory _memory = new();

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
        try
        {
            EvmPooledMemoryBenchmarkHelper.MStore(_memory, MemorySize - EvmPooledMemory.WordSize);
            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }

    [Benchmark]
    public ulong SequentialGrowth()
    {
        try
        {
            for (int offset = 0; offset < MemorySize; offset += EvmPooledMemory.WordSize)
            {
                EvmPooledMemoryBenchmarkHelper.MStore(_memory, offset);
            }

            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "MSTORE")]
public class EvmPooledMemoryFirstMStoreBenchmarks
{
    private const int FourKiB = 4 * 1024;
    private const int SixtyFourKiB = 64 * 1024;
    private readonly EvmPooledMemory _memory = new();

    [Params(0, 64, FourKiB, SixtyFourKiB)]
    public int Offset { get; set; }

    [Benchmark]
    public ulong FirstMStore()
    {
        try
        {
            EvmPooledMemoryBenchmarkHelper.MStore(_memory, Offset);
            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }

    [Benchmark]
    public ulong JumpThenContiguousMStore()
    {
        try
        {
            EvmPooledMemoryBenchmarkHelper.MStore(_memory, Offset);
            EvmPooledMemoryBenchmarkHelper.MStore(_memory, Offset + EvmPooledMemory.WordSize);
            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "MSTORE", "Solidity")]
public class EvmPooledMemorySolidityLifecycleBenchmarks
{
    private readonly EvmPooledMemory _memory = new();

    [Benchmark]
    public ulong PrologueOnly()
    {
        EvmPooledMemoryBenchmarkHelper.MStore(_memory, 0x40);
        ulong size = _memory.Size;
        _memory.Dispose();
        return size;
    }

    [Benchmark]
    public ulong PrologueThenScratch()
    {
        EvmPooledMemoryBenchmarkHelper.MStore(_memory, 0x40);
        EvmPooledMemoryBenchmarkHelper.MStore(_memory, 0x00);
        EvmPooledMemoryBenchmarkHelper.MStore(_memory, 0x20);
        ulong size = _memory.Size;
        _memory.Dispose();
        return size;
    }

    [Benchmark]
    public ulong PrologueThenReturnWord()
    {
        EvmPooledMemoryBenchmarkHelper.MStore(_memory, 0x40);
        EvmPooledMemoryBenchmarkHelper.MStore(_memory, 0x80);
        ulong size = _memory.Size;
        _memory.Dispose();
        return size;
    }

    [Benchmark]
    public ulong PrologueThenLargerAllocation()
    {
        EvmPooledMemoryBenchmarkHelper.MStore(_memory, 0x40);
        EvmPooledMemoryBenchmarkHelper.MStore(_memory, 0x100);
        ulong size = _memory.Size;
        _memory.Dispose();
        return size;
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "MSTORE8")]
public class EvmPooledMemoryMStore8Benchmarks
{
    private const int FourKiB = 4 * 1024;
    private const int OneMiB = 1 << 20;
    private readonly EvmPooledMemory _memory = new();

    [Params(1, 256, FourKiB, FourKiB + 1, OneMiB)]
    public int MemorySize { get; set; }

    [Benchmark]
    public ulong HighWaterJump()
    {
        try
        {
            EvmPooledMemoryBenchmarkHelper.MStore8(_memory, MemorySize - 1);
            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }

    [Benchmark]
    public ulong SequentialGrowth()
    {
        try
        {
            for (int offset = 0; offset < MemorySize; offset++)
            {
                EvmPooledMemoryBenchmarkHelper.MStore8(_memory, offset);
            }

            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }
}
