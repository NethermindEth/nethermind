// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
public unsafe class Int256ExpAbBenchmarks
{
    private const int Count = 1024;

    private UInt256* _b;
    private UInt256* _e;
    private UInt256* _out;

    [Params("Odd256x8", "Odd256x9", "Odd256x12", "Odd256x16", "Odd256x16Dense", "Odd256x24", "Odd256x32",
        "Odd256x40", "Odd256x48", "Odd256x64", "Odd256x64Sparse", "Odd256x64Dense", "Odd256x72", "Odd256x79",
        "Odd256x256", "Base3x8", "Base3x12", "Base3x16", "Base3x24", "Base3x32", "Base3x40", "Base3x64", "Base3x79",
        "Pow2Mixed", "Pow2Fixed")]
    public string Shape { get; set; } = "Odd256x8";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0xE7E7);
        _b = (UInt256*)NativeMemory.AlignedAlloc((nuint)(Count * sizeof(UInt256)), 64);
        _e = (UInt256*)NativeMemory.AlignedAlloc((nuint)(Count * sizeof(UInt256)), 64);
        _out = (UInt256*)NativeMemory.AlignedAlloc((nuint)(Count * sizeof(UInt256)), 64);
        for (int i = 0; i < Count; i++)
        {
            (UInt256 b, UInt256 e) = Shape switch
            {
                "Pow2Mixed" => (new UInt256(random.Next(2) == 0 ? 256UL : 2UL), new UInt256(Pick(random, 20, 28, 16, 2, 160, 176, 224))),
                "Pow2Fixed" => (new UInt256(256), new UInt256(20)),
                _ when Shape.StartsWith("Base3x", StringComparison.Ordinal) => (new UInt256(3), Exponent(random, int.Parse(Shape.AsSpan(6)))),
                _ when Shape.EndsWith("Dense", StringComparison.Ordinal) => (Odd256(random), Exponent(random, Bits(Shape, 5)) | Exponent(random, Bits(Shape, 5))),
                _ when Shape.EndsWith("Sparse", StringComparison.Ordinal) => (Odd256(random), Exponent(random, Bits(Shape, 6)) & Exponent(random, Bits(Shape, 6))),
                _ => (Odd256(random), Exponent(random, Bits(Shape, 0))),
            };
            _b[i] = b;
            _e[i] = e;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NativeMemory.AlignedFree(_b);
        NativeMemory.AlignedFree(_e);
        NativeMemory.AlignedFree(_out);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void Exp()
    {
        for (int i = 0; i < Count; i++) Exp(in _b[i], in _e[i], ref _out[i]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Exp(in UInt256 b, in UInt256 e, ref UInt256 slot)
    {
        UInt256.Exp(in b, in e, out UInt256 result);
        Unsafe.WriteUnaligned(ref Unsafe.As<UInt256, byte>(ref slot), result);
    }

    private static int Bits(string shape, int suffix) => int.Parse(shape.AsSpan(7, shape.Length - 7 - suffix));

    private static ulong Pick(Random random, params int[] values) => (ulong)values[random.Next(values.Length)];

    private static ulong Next64(Random random) => ((ulong)random.NextInt64() << 1) | (uint)random.Next(2);

    private static UInt256 Random256(Random random) =>
        new(Next64(random), Next64(random), Next64(random), Next64(random) | (1UL << 63));

    private static UInt256 Odd256(Random random)
    {
        UInt256 x = Random256(random);
        return new UInt256(x.u0 | 1, x.u1, x.u2, x.u3);
    }

    private static UInt256 Exponent(Random random, int bits)
    {
        UInt256 x = new(Next64(random), Next64(random), Next64(random), Next64(random));
        x = bits >= 256 ? x : x & ((UInt256.One << bits) - 1);
        return x | (UInt256.One << (bits - 1));
    }
}
