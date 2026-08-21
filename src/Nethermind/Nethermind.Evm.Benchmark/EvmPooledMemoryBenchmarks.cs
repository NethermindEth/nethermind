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

    private static readonly byte[] FourKiBPayload = CreatePayload(FourKiB);
    private static readonly byte[] OneMiBPayload = CreatePayload(OneMiB);

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

    private static byte[] CreatePayload(int length)
    {
        byte[] payload = new byte[length];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 37 + 0x41);
        }

        return payload;
    }
}
