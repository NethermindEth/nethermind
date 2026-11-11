// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

// Wei amounts turned into decimal ether, as the block and payload log lines and the RPC fee cap check write it,
// and a decimal turned back into UInt256. Base and head differ only by the referenced Int256 package.
[MemoryDiagnoser]
public class Int256ConversionAbBenchmarks
{
    private const int Count = 1024;
    private const decimal WeiPerEther = 1_000_000_000_000_000_000m;

    private UInt256[] _fees = [];
    private decimal[] _averages = [];

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0xDEC0);
        _fees = new UInt256[Count];
        _averages = new decimal[Count];
        for (int i = 0; i < Count; i++)
        {
            ulong low = (ulong)random.NextInt64();
            _fees[i] = (i & 3) == 0 ? new UInt256(low, (ulong)random.Next(1 << 16)) : new UInt256(low >> random.Next(0, 32));
            _averages[i] = new decimal(random.Next(), random.Next(), random.Next(1 << 8), false, (byte)random.Next(0, 6));
        }
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public decimal FeeInEther()
    {
        decimal acc = 0;
        UInt256[] fees = _fees;
        for (int i = 0; i < fees.Length; i++)
        {
            acc += ToEther(in fees[i]);
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public decimal DecimalCast()
    {
        decimal acc = 0;
        UInt256[] fees = _fees;
        for (int i = 0; i < fees.Length; i++)
        {
            acc += Narrow(in fees[i]);
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong FromDecimal()
    {
        ulong acc = 0;
        decimal[] averages = _averages;
        for (int i = 0; i < averages.Length; i++)
        {
            UInt256 value = Widen(averages[i]);
            acc ^= value.u0 ^ value.u1;
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static decimal ToEther(in UInt256 fee) => fee.ToDecimal(null) / WeiPerEther;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static decimal Narrow(in UInt256 fee) => (decimal)fee;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UInt256 Widen(decimal value) => (UInt256)value;
}
