// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory")]
public class EvmPooledMemoryBenchmarks
{
    private const int FourKiB = 4 * 1024;
    private const int OneMiB = 1 << 20;
    private const int SmallPayloadSize = 97;

    private static readonly byte[] FourKiBPayload = EvmPooledMemoryBenchmarkHelper.CreatePayload(FourKiB);
    private static readonly byte[] OneMiBPayload = EvmPooledMemoryBenchmarkHelper.CreatePayload(OneMiB);
    private static readonly byte[] SmallPayload = EvmPooledMemoryBenchmarkHelper.CreatePayload(SmallPayloadSize);

    [Benchmark]
    public ulong SmallUnalignedExternalOverwrite()
    {
        EvmPooledMemory memory = default;
        try
        {
            UInt256 destination = UInt256.Zero;
            memory.TrySave(in destination, SmallPayload);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Benchmark]
    public ulong FullOneMiBExternalOverwrite()
    {
        EvmPooledMemory memory = default;
        try
        {
            UInt256 destination = UInt256.Zero;
            memory.TrySave(in destination, OneMiBPayload);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Benchmark]
    public ulong GappedOneMiBExternalOverwrite()
    {
        EvmPooledMemory memory = default;
        try
        {
            UInt256 first = UInt256.Zero;
            memory.TrySaveByte(in first, 0x5a);
            UInt256 destination = 2 * FourKiB;
            memory.TrySave(in destination, OneMiBPayload);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Benchmark]
    public ulong AlreadyExpandedFourKiBOverwrite()
    {
        EvmPooledMemory memory = default;
        try
        {
            UInt256 end = 2 * FourKiB - 1;
            memory.TrySaveByte(in end, 0xad);
            UInt256 destination = FourKiB;
            memory.TrySave(in destination, FourKiBPayload);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Benchmark]
    public ulong ZeroExtendedCopyIntoExpansion()
    {
        EvmPooledMemory memory = default;
        try
        {
            UInt256 first = UInt256.Zero;
            memory.TrySaveByte(in first, 0x5a);

            UInt256 destination = 2 * FourKiB;
            const int length = OneMiB + FourKiB;
            memory.CalculateMemoryCost(in destination, length, out _);
            UInt256 sourceOffset = UInt256.Zero;
            memory.CopyFromZeroExtendedAfterGas(
                in destination,
                OneMiBPayload,
                in sourceOffset,
                length);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Benchmark]
    public ulong MCopyIntoExpansion()
    {
        EvmPooledMemory memory = default;
        try
        {
            UInt256 source = UInt256.Zero;
            memory.TrySave(in source, OneMiBPayload);

            UInt256 destination = OneMiB;
            memory.CalculateMemoryCost(in destination, OneMiB, out _);
            memory.CopyAfterGas(in destination, in source, OneMiB);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "OverwriteSlow")]
public class EvmPooledMemoryOverwriteSlowPathBenchmarks
{
    private static readonly byte[] Word = EvmPooledMemoryBenchmarkHelper.CreatePayload(EvmPooledMemory.WordSize);
    private readonly VmState<EthereumGasPolicy> _state = new();

    [Benchmark]
    public ulong InlineGapOverwrite()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        UInt256 destination = 0x40;
        memory.CalculateMemoryCost(in destination, EvmPooledMemory.WordSize, out _);
        memory.SaveAfterGas(in destination, Word);
        ulong size = memory.Size;
        memory.Dispose();
        return size;
    }

    [Benchmark]
    public ulong InlinePrefixThenFirstSpill()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0x40);
        UInt256 destination = 0x400;
        memory.CalculateMemoryCost(in destination, EvmPooledMemory.WordSize, out _);
        memory.SaveAfterGas(in destination, Word);
        ulong size = memory.Size;
        memory.Dispose();
        return size;
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "MCOPY")]
public class EvmPooledMemoryMCopyBenchmarks
{
    private EvmPooledMemory _memory;
    private UInt256 _source;
    private UInt256 _destination;
    private UInt256 _overlappingDestination;
    private UInt256 _partialSource;

    [Params(EvmPooledMemory.WordSize, 4 * 1024)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        int initializedLength = System.Math.Max(2 * Length, 1024);
        byte[] payload = EvmPooledMemoryBenchmarkHelper.CreatePayload(initializedLength);
        UInt256 start = UInt256.Zero;
        _memory.TrySave(in start, payload);
        _source = UInt256.Zero;
        _destination = (UInt256)Length;
        _overlappingDestination = (UInt256)(Length / 2);
        _partialSource = (UInt256)(initializedLength - Length / 2);
        _memory.CalculateMemoryCost(in _partialSource, (ulong)Length, out _);
    }

    [GlobalCleanup]
    public void Cleanup() => _memory.Dispose();

    [Benchmark]
    public ulong FullyInitializedNonOverlapping()
    {
        _memory.CopyAfterGas(in _destination, in _source, (ulong)Length);
        return _memory.Size;
    }

    [Benchmark]
    public ulong FullyInitializedOverlapRight()
    {
        _memory.CopyAfterGas(in _overlappingDestination, in _source, (ulong)Length);
        return _memory.Size;
    }

    [Benchmark]
    public ulong PartiallyInitializedSource()
    {
        _memory.CopyAfterGas(in _source, in _partialSource, (ulong)Length);
        return _memory.Size;
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "OverwriteGrowth")]
public class EvmPooledMemoryOverwriteGrowthBenchmarks
{
    private const int FourKiB = 4 * 1024;
    private const int OneMiB = 1 << 20;

    private static readonly byte[] InitialPayload = EvmPooledMemoryBenchmarkHelper.CreatePayload(OneMiB);
    private static readonly byte[] OverwritePayload = EvmPooledMemoryBenchmarkHelper.CreatePayload(2 * OneMiB);

    [Params(0, FourKiB, OneMiB)]
    public int DestinationOffset { get; set; }

    [Benchmark]
    public ulong GrowForOverwrite()
    {
        EvmPooledMemory memory = default;
        try
        {
            UInt256 start = UInt256.Zero;
            memory.TrySave(in start, InitialPayload);

            UInt256 destination = (UInt256)DestinationOffset;
            memory.TrySave(in destination, OverwritePayload);
            return memory.Size;
        }
        finally
        {
            memory.Dispose();
        }
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "ReadMaterialization")]
public class EvmPooledMemoryReadMaterializationBenchmarks
{
    private const int SolidityPrologueOffset = 0x40;
    private const int InitializedEnd = SolidityPrologueOffset + EvmPooledMemory.WordSize;
    private const int CommonReservationSize = 512;
    private const int SpilledReservationSize = 4 * 1024;

    private readonly VmState<EthereumGasPolicy> _state = new();

    [Benchmark(Baseline = true)]
    public ulong InitializedWord()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, SolidityPrologueOffset);
        ulong result = ReadWord(ref memory, SolidityPrologueOffset);
        memory.Dispose();
        return result;
    }

    [Benchmark]
    public ulong Reserved512ThenInitializedWord()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, SolidityPrologueOffset);
        Reserve(ref memory, CommonReservationSize);
        ulong result = ReadWord(ref memory, SolidityPrologueOffset);
        memory.Dispose();
        return result;
    }

    [Benchmark]
    public ulong Reserved4096ThenInitializedWord()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, SolidityPrologueOffset);
        Reserve(ref memory, SpilledReservationSize);
        ulong result = ReadWord(ref memory, SolidityPrologueOffset);
        memory.Dispose();
        return result;
    }

    [Benchmark]
    public ulong Reserved512ThenInitializedCallInput()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, SolidityPrologueOffset);
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, 0);
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, EvmPooledMemory.WordSize);
        Reserve(ref memory, CommonReservationSize);

        UInt256 inputStart = UInt256.Zero;
        Span<byte> input = memory.LoadSpanAfterGas(in inputStart, InitializedEnd);
        ulong result = memory.Size + input[0];
        memory.Dispose();
        return result;
    }

    [Benchmark]
    public ulong Reserved512ThenCrossingWord()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, SolidityPrologueOffset);
        Reserve(ref memory, CommonReservationSize);
        ulong result = ReadWord(ref memory, InitializedEnd);
        memory.Dispose();
        return result;
    }

    [Benchmark]
    public ulong SequentialZeroWords()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        Reserve(ref memory, EvmPooledMemory.InlineCapacity);

        byte result = 0;
        for (int offset = 0; offset < EvmPooledMemory.InlineCapacity; offset += EvmPooledMemory.WordSize)
        {
            UInt256 location = (UInt256)offset;
            result ^= memory.Load32BytesAfterGas(in location);
        }

        ulong size = memory.Size + result;
        memory.Dispose();
        return size;
    }

    [Benchmark]
    public ulong Reserved4096ThenInitializedArrayWord()
    {
        ref EvmPooledMemory memory = ref _state.Memory;
        EvmPooledMemoryBenchmarkHelper.MStore(ref memory, EvmPooledMemory.InlineCapacity);
        Reserve(ref memory, SpilledReservationSize);
        ulong result = ReadWord(ref memory, SolidityPrologueOffset);
        memory.Dispose();
        return result;
    }

    private static void Reserve(ref EvmPooledMemory memory, int size)
    {
        UInt256 start = UInt256.Zero;
        memory.CalculateMemoryCost(in start, (ulong)size, out _);
    }

    private static ulong ReadWord(ref EvmPooledMemory memory, int offset)
    {
        UInt256 location = (UInt256)offset;
        return memory.Size + memory.Load32BytesAfterGas(in location);
    }
}
