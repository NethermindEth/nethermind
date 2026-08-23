// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
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
    private readonly EvmPooledMemory _memory = new();

    [Benchmark]
    public ulong SmallUnalignedExternalOverwrite()
    {
        try
        {
            UInt256 destination = UInt256.Zero;
            _memory.TrySave(in destination, SmallPayload);
            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }

    [Benchmark]
    public ulong FullOneMiBExternalOverwrite()
    {
        try
        {
            UInt256 destination = UInt256.Zero;
            _memory.TrySave(in destination, OneMiBPayload);
            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }

    [Benchmark]
    public ulong GappedOneMiBExternalOverwrite()
    {
        try
        {
            UInt256 first = UInt256.Zero;
            _memory.TrySaveByte(in first, 0x5a);
            UInt256 destination = 2 * FourKiB;
            _memory.TrySave(in destination, OneMiBPayload);
            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }

    [Benchmark]
    public ulong AlreadyExpandedFourKiBOverwrite()
    {
        try
        {
            UInt256 end = 2 * FourKiB - 1;
            _memory.TrySaveByte(in end, 0xad);
            UInt256 destination = FourKiB;
            _memory.TrySave(in destination, FourKiBPayload);
            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }

    [Benchmark]
    public ulong ZeroExtendedCopyIntoExpansion()
    {
        try
        {
            UInt256 first = UInt256.Zero;
            _memory.TrySaveByte(in first, 0x5a);

            UInt256 destination = 2 * FourKiB;
            const int length = OneMiB + FourKiB;
            _memory.CalculateMemoryCost(in destination, length, out _);
            UInt256 sourceOffset = UInt256.Zero;
            _memory.CopyFromZeroExtendedAfterGas(
                in destination,
                OneMiBPayload,
                in sourceOffset,
                length);
            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }

    [Benchmark]
    public ulong MCopyIntoExpansion()
    {
        try
        {
            UInt256 source = UInt256.Zero;
            _memory.TrySave(in source, OneMiBPayload);

            UInt256 destination = OneMiB;
            _memory.CalculateMemoryCost(in destination, OneMiB, out _);
            _memory.CopyAfterGas(in destination, in source, OneMiB);
            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "MCOPY")]
public class EvmPooledMemoryMCopyBenchmarks
{
    private readonly EvmPooledMemory _memory = new();
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
    private readonly EvmPooledMemory _memory = new();

    [Params(0, FourKiB, OneMiB)]
    public int DestinationOffset { get; set; }

    [Benchmark]
    public ulong GrowForOverwrite()
    {
        try
        {
            UInt256 start = UInt256.Zero;
            _memory.TrySave(in start, InitialPayload);

            UInt256 destination = (UInt256)DestinationOffset;
            _memory.TrySave(in destination, OverwritePayload);
            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("EVM", "Memory", "LazyOverwriteTail")]
public class EvmPooledMemoryLazyOverwriteTailBenchmarks
{
    private const int PayloadLength = 4 * EvmPooledMemory.WordSize;

    private static readonly byte[] Payload = EvmPooledMemoryBenchmarkHelper.CreatePayload(PayloadLength);
    private static readonly byte[] Word = EvmPooledMemoryBenchmarkHelper.CreatePayload(EvmPooledMemory.WordSize);
    private readonly EvmPooledMemory _memory = new();

    [Params(0, 1, 128)]
    public int FollowingWordCount { get; set; }

    [Benchmark]
    public ulong OverwriteThenMStore()
    {
        try
        {
            UInt256 start = UInt256.Zero;
            _memory.TrySave(in start, Payload);

            for (int i = 0; i < FollowingWordCount; i++)
            {
                UInt256 destination = (UInt256)(PayloadLength + i * EvmPooledMemory.WordSize);
                _memory.TrySaveWord(in destination, Word);
            }

            return _memory.Size;
        }
        finally
        {
            _memory.Dispose();
        }
    }
}
