// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

// Fee and balance arithmetic as transaction validation and block building write it with the Int256 operators,
// and the NumberStyles.None parse the JSON reader uses for numbers wider than a ulong. Base and head differ
// only by the referenced Int256 package.
[MemoryDiagnoser]
public unsafe class Int256OperatorAbBenchmarks
{
    private const int Count = 1024;

    private UInt256* _balance;
    private UInt256* _price;
    private UInt256* _wideX;
    private UInt256* _wideY;
    private long[] _gas = [];
    private string[] _texts = [];

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0x0B5E);
        _balance = Int256LibraryAbBenchmarks.Allocate();
        _price = Int256LibraryAbBenchmarks.Allocate();
        _wideX = Int256LibraryAbBenchmarks.Allocate();
        _wideY = Int256LibraryAbBenchmarks.Allocate();
        _gas = new long[Count];
        _texts = new string[Count];
        Span<byte> bytes = stackalloc byte[32];
        for (int i = 0; i < Count; i++)
        {
            long gas = random.NextInt64(21_000, 30_000_000);
            UInt256 price = new((ulong)random.NextInt64(1_000_000_000, 500_000_000_000));
            _gas[i] = gas;
            _price[i] = price;
            _balance[i] = (UInt256)gas * price + new UInt256((ulong)random.NextInt64(), (ulong)random.Next(1 << 20));

            random.NextBytes(bytes);
            UInt256 y = new(bytes, isBigEndian: false);
            random.NextBytes(bytes);
            UInt256 delta = new(bytes, isBigEndian: false);
            delta >>= 1;
            y >>= 1;
            _wideY[i] = y;
            _wideX[i] = y + delta;

            random.NextBytes(bytes);
            string digits = new UInt256(bytes[..random.Next(9, 17)], isBigEndian: true).ToString();
            _texts[i] = (i & 1) == 0 ? digits : digits + "\0";
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NativeMemory.AlignedFree(_balance);
        NativeMemory.AlignedFree(_price);
        NativeMemory.AlignedFree(_wideX);
        NativeMemory.AlignedFree(_wideY);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong FeeArithmetic()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            UInt256 remaining = Remaining(in _balance[i], _gas[i], in _price[i]);
            acc ^= remaining.u0 ^ remaining.u1;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong SubtractOperator()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            UInt256 difference = Difference(in _wideX[i], in _wideY[i]);
            acc ^= difference.u0 ^ difference.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong LongConversion()
    {
        ulong acc = 0;
        long[] gas = _gas;
        for (int i = 0; i < gas.Length; i++)
        {
            UInt256 value = Widen(gas[i]);
            acc ^= value.u0 ^ value.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong ParseDigits()
    {
        ulong acc = 0;
        string[] texts = _texts;
        for (int i = 0; i < texts.Length; i++)
        {
            UInt256 value = UInt256.Parse(texts[i].AsSpan(), NumberStyles.None);
            acc ^= value.u0 ^ value.u1;
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UInt256 Remaining(in UInt256 balance, long gas, in UInt256 price) => balance - (UInt256)gas * price;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UInt256 Difference(in UInt256 x, in UInt256 y) => x - y;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UInt256 Widen(long value) => (UInt256)value;
}
