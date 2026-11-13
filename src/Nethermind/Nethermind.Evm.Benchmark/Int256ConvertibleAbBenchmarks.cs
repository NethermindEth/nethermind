// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

using Int256 = Nethermind.Int256.Int256;

// The IConvertible and IFormatProvider members of UInt256 and Int256, as a caller that holds the value through
// IConvertible or formats with an explicit culture reaches them. Base and head differ only by the referenced Int256
// package.
[MemoryDiagnoser]
public class Int256ConvertibleAbBenchmarks
{
    private const int Count = 1024;

    private UInt256[] _unsigned = [];
    private Int256[] _signed = [];
    private Int256[] _narrow = [];
    private readonly IFormatProvider _provider = CultureInfo.InvariantCulture;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0xC0E5);
        _unsigned = new UInt256[Count];
        _signed = new Int256[Count];
        _narrow = new Int256[Count];
        for (int i = 0; i < Count; i++)
        {
            ulong low = (ulong)random.NextInt64();
            UInt256 value = (i & 3) == 0 ? new UInt256(low, (ulong)random.Next(1 << 16)) : new UInt256(low >> random.Next(0, 32));
            _unsigned[i] = value;
            Int256 signed = new(value);
            _signed[i] = (i & 1) == 0 ? signed : -signed;
            long narrow = random.NextInt64(long.MinValue, long.MaxValue) >> random.Next(0, 32);
            _narrow[i] = narrow;
        }
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public int UnsignedToStringProvider()
    {
        int acc = 0;
        UInt256[] values = _unsigned;
        for (int i = 0; i < values.Length; i++)
        {
            acc += FormatUnsigned(in values[i], _provider).Length;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public int SignedToStringProvider()
    {
        int acc = 0;
        Int256[] values = _signed;
        for (int i = 0; i < values.Length; i++)
        {
            acc += FormatSigned(in values[i], _provider).Length;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public decimal SignedToDecimal()
    {
        decimal acc = 0;
        Int256[] values = _signed;
        for (int i = 0; i < values.Length; i++)
        {
            acc += SignedDecimal(in values[i], _provider);
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public double SignedToDouble()
    {
        double acc = 0;
        Int256[] values = _signed;
        for (int i = 0; i < values.Length; i++)
        {
            acc += SignedDouble(in values[i], _provider);
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public long SignedToInt64()
    {
        long acc = 0;
        Int256[] values = _narrow;
        for (int i = 0; i < values.Length; i++)
        {
            acc ^= SignedInt64(in values[i], _provider);
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public float UnsignedToSingle()
    {
        float acc = 0;
        UInt256[] values = _unsigned;
        for (int i = 0; i < values.Length; i++)
        {
            acc += UnsignedSingle(in values[i], _provider);
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string FormatUnsigned(in UInt256 value, IFormatProvider provider) => value.ToString(provider);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string FormatSigned(in Int256 value, IFormatProvider provider) => value.ToString(provider);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static decimal SignedDecimal(in Int256 value, IFormatProvider provider) => value.ToDecimal(provider);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double SignedDouble(in Int256 value, IFormatProvider provider) => value.ToDouble(provider);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long SignedInt64(in Int256 value, IFormatProvider provider) => value.ToInt64(provider);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float UnsignedSingle(in UInt256 value, IFormatProvider provider) => value.ToSingle(provider);
}
