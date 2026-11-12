// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

using Int256 = Nethermind.Int256.Int256;

// SDIV and SMOD as the opcode handlers call them, on words reinterpreted in place, and the UInt256 arithmetic
// operators that fee and balance code uses. Base and head differ only by the referenced Int256 package.
[MemoryDiagnoser]
public unsafe class Int256SignedOperatorAbBenchmarks
{
    private const int Count = 1024;

    private UInt256* _x;
    private UInt256* _y;

    // Positive: both operands non-negative, 128-bit dividend and 64-bit divisor.
    // MixedSmall: random signs with magnitudes below 2^64, as int24 to int128 fixed-point math leaves them.
    // MixedWide: random signs with magnitudes of 129 to 255 bits.
    [Params("Positive", "MixedSmall", "MixedWide")]
    public string Shape { get; set; } = "Positive";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0x5D1F);
        _x = Int256LibraryAbBenchmarks.Allocate();
        _y = Int256LibraryAbBenchmarks.Allocate();
        for (int i = 0; i < Count; i++)
        {
            UInt256 x;
            UInt256 y;
            switch (Shape)
            {
                case "Positive":
                    x = new UInt256((ulong)random.NextInt64(), (ulong)random.NextInt64(1L << 40));
                    y = new UInt256((ulong)random.NextInt64(2, long.MaxValue));
                    break;
                case "MixedSmall":
                    x = Signed(new UInt256((ulong)random.NextInt64(1, long.MaxValue)), random);
                    y = Signed(new UInt256((ulong)random.NextInt64(2, 1L << 32)), random);
                    break;
                default:
                    x = Signed(Random256(random) >> random.Next(1, 64), random);
                    y = Signed(Random256(random) >> random.Next(64, 127), random);
                    break;
            }

            _x[i] = x;
            _y[i] = y;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NativeMemory.AlignedFree(_x);
        NativeMemory.AlignedFree(_y);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong SignedDivide()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            SignedDivide(in _x[i], in _y[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong SignedMod()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            SignedMod(in _x[i], in _y[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong MultiplyOperator()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            UInt256 product = Product(in _x[i], in _y[i]);
            acc ^= product.u0 ^ product.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong AddOperator()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            UInt256 sum = Sum(in _x[i], in _y[i]);
            acc ^= sum.u0 ^ sum.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong DivideOperator()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            UInt256 quotient = Quotient(in _x[i], in _y[i]);
            acc ^= quotient.u0 ^ quotient.u3;
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SignedDivide(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        Unsafe.SkipInit(out result);
        Int256.Divide(
            in Unsafe.As<UInt256, Int256>(ref Unsafe.AsRef(in a)),
            in Unsafe.As<UInt256, Int256>(ref Unsafe.AsRef(in b)),
            out Unsafe.As<UInt256, Int256>(ref result));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SignedMod(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        Unsafe.SkipInit(out result);
        Unsafe.As<UInt256, Int256>(ref Unsafe.AsRef(in a)).Mod(
            in Unsafe.As<UInt256, Int256>(ref Unsafe.AsRef(in b)),
            out Unsafe.As<UInt256, Int256>(ref result));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UInt256 Product(in UInt256 x, in UInt256 y) => x * y;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UInt256 Sum(in UInt256 x, in UInt256 y) => x + y;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UInt256 Quotient(in UInt256 x, in UInt256 y) => x / y;

    private static UInt256 Signed(in UInt256 magnitude, Random random)
    {
        if (random.Next(2) == 0) return magnitude;
        UInt256.Subtract(default, in magnitude, out UInt256 negative);
        return negative;
    }

    private static UInt256 Random256(Random random)
    {
        Span<byte> bytes = stackalloc byte[32];
        random.NextBytes(bytes);
        return new UInt256(bytes, isBigEndian: false);
    }
}
