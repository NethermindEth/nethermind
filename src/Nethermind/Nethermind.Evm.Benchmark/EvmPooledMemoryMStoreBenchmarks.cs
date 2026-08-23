// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Evm.GasPolicy;

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

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "MSTORE")]
public class EvmPooledMemoryFirstMStoreBenchmarks
{
    private const int FourKiB = 4 * 1024;
    private const int SixtyFourKiB = 64 * 1024;

    [Params(0, 64, FourKiB, SixtyFourKiB)]
    public int Offset { get; set; }

    [Benchmark]
    public ulong FirstMStore()
    {
        EvmPooledMemory memory = default;
        try
        {
            EvmPooledMemoryBenchmarkHelper.MStore(ref memory, Offset);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Benchmark]
    public ulong JumpThenContiguousMStore()
    {
        EvmPooledMemory memory = default;
        try
        {
            EvmPooledMemoryBenchmarkHelper.MStore(ref memory, Offset);
            EvmPooledMemoryBenchmarkHelper.MStore(ref memory, Offset + EvmPooledMemory.WordSize);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "MSTORE", "Solidity")]
public class EvmPooledMemorySolidityLifecycleBenchmarks
{
    private readonly VmState<EthereumGasPolicy> _state = new();

    [Benchmark]
    public ulong PrologueOnly()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x40);
        ulong size = memory.Size;
        memory.Dispose();
        return size;
    }

    [Benchmark]
    public ulong PrologueThenScratch()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x40);
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x00);
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x20);
        ulong size = memory.Size;
        memory.Dispose();
        return size;
    }

    [Benchmark]
    public ulong PrologueThenReturnWord()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x40);
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x80);
        ulong size = memory.Size;
        memory.Dispose();
        return size;
    }

    [Benchmark]
    public ulong PrologueThenLargerAllocation()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x40);
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x100);
        ulong size = memory.Size;
        memory.Dispose();
        return size;
    }

    [Benchmark]
    public ulong PrologueThenWordAt0x120()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x40);
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x120);
        ulong size = memory.Size;
        memory.Dispose();
        return size;
    }

    [Benchmark]
    public ulong PrologueThenFirstSpill()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x40);
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x400);
        ulong size = memory.Size;
        memory.Dispose();
        return size;
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "MSTORE8")]
public class EvmPooledMemoryMStore8Benchmarks
{
    private const int FourKiB = 4 * 1024;
    private const int OneMiB = 1 << 20;

    [Params(1, 256, FourKiB, FourKiB + 1, OneMiB)]
    public int MemorySize { get; set; }

    [Benchmark]
    public ulong HighWaterJump()
    {
        EvmPooledMemory memory = default;
        try
        {
            EvmPooledMemoryBenchmarkHelper.MStore8(ref memory, MemorySize - 1);
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
            for (int offset = 0; offset < MemorySize; offset++)
            {
                EvmPooledMemoryBenchmarkHelper.MStore8(ref memory, offset);
            }

            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }
}
