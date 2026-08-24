// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

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
    public ulong JumpThenContiguousMStore()
    {
        EvmPooledMemory memory = default;
        try
        {
            int offset = MemorySize - EvmPooledMemory.WordSize;
            EvmPooledMemoryBenchmarkHelper.MStore(ref memory, offset);
            EvmPooledMemoryBenchmarkHelper.MStore(ref memory, offset + EvmPooledMemory.WordSize);
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
[BenchmarkCategory("EVM", "Memory", "MSTORE", "ReservedTail")]
public class EvmPooledMemoryReservedMStoreBenchmarks
{
    private const int ReservationSize = 4 * 1024;
    private const int GapOffset = 256;

    private static readonly byte[] Word = EvmPooledMemoryBenchmarkHelper.CreatePayload(EvmPooledMemory.WordSize);

    [Params(1, 16)]
    public int WordCount { get; set; }

    [Benchmark(Baseline = true)]
    public ulong InitializedPrefix()
    {
        EvmPooledMemory memory = default;
        try
        {
            EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0);
            WriteWords(ref memory, 0);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Benchmark]
    public ulong ReservedInitializedPrefix()
    {
        EvmPooledMemory memory = default;
        try
        {
            EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0);
            Reserve(ref memory);
            WriteWords(ref memory, 0);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Benchmark]
    public ulong ReservedContiguousGrowth()
    {
        EvmPooledMemory memory = default;
        try
        {
            WriteLazyPrefix(ref memory);
            Reserve(ref memory);
            WriteWords(ref memory, EvmPooledMemory.WordSize);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Benchmark]
    public ulong ReservedGapGrowth()
    {
        EvmPooledMemory memory = default;
        try
        {
            WriteLazyPrefix(ref memory);
            Reserve(ref memory);
            WriteWords(ref memory, GapOffset);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    private void WriteWords(ref EvmPooledMemory memory, int start)
    {
        for (int i = 0; i < WordCount; i++)
        {
            EvmPooledMemoryBenchmarkHelper.MStore(ref memory, start + i * EvmPooledMemory.WordSize);
        }
    }

    private static void WriteLazyPrefix(ref EvmPooledMemory memory)
    {
        UInt256 start = UInt256.Zero;
        memory.CalculateMemoryCost(in start, EvmPooledMemory.WordSize, out _);
        memory.SaveAfterGas(in start, Word);
    }

    private static void Reserve(ref EvmPooledMemory memory)
    {
        UInt256 start = UInt256.Zero;
        memory.CalculateMemoryCost(in start, ReservationSize, out _);
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

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "MSTORE8", "ReservedTail")]
public class EvmPooledMemoryReservedMStore8Benchmarks
{
    private const int ReservationSize = 4 * 1024;

    private EvmPooledMemory _initialized;
    private EvmPooledMemory _reserved;

    [Params(1, 256)]
    public int ByteCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        EvmPooledMemoryBenchmarkHelper.MStore8(ref _initialized, 0);
        EvmPooledMemoryBenchmarkHelper.MStore8(ref _reserved, 0);
        UInt256 start = UInt256.Zero;
        _reserved.CalculateMemoryCost(in start, ReservationSize, out _);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _initialized.Dispose();
        _reserved.Dispose();
    }

    [Benchmark(Baseline = true)]
    public ulong InitializedPrefix() => WriteBytes(ref _initialized);

    [Benchmark]
    public ulong ReservedInitializedPrefix() => WriteBytes(ref _reserved);

    private ulong WriteBytes(ref EvmPooledMemory memory)
    {
        for (int offset = 0; offset < ByteCount; offset++)
        {
            EvmPooledMemoryBenchmarkHelper.MStore8(ref memory, offset);
        }

        return memory.Size;
    }
}
