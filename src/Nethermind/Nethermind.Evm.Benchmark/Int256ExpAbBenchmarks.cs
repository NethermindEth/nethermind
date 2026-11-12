// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

// UInt256.Exp as the EXP handler calls it: the handler answers a zero exponent and bases 0 and 1 itself, so no shape
// holds them. Base and head differ only by the referenced Int256 package.
[MemoryDiagnoser]
public unsafe class Int256ExpAbBenchmarks
{
    private const int Count = 1024;

    private UInt256* _b;
    private UInt256* _e;

    // MainnetMix: bases and exponents drawn with the weights seen on mainnet blocks (powers of two, ten, rare others).
    // Pow2Mixed / Pow2Fixed: bases 2^8 and 2^1 with mixed exponents, or 2^8 with exponent 20 only.
    // Ten: base 10 with exponents 1 to 36. NarrowSquare: 64- to 96-bit bases with exponent 2.
    // Odd256xN: random odd 256-bit base with a random N-bit exponent. Base3x32, Base7x16: small bases.
    // OnePlus2kx32: base 2^k + 1 (k in 8..60) with a 32-bit exponent. Wide256Square: random 256-bit base, exponent 2.
    [Params("MainnetMix", "Pow2Mixed", "Pow2Fixed", "Ten", "NarrowSquare", "Odd256x8", "Odd256x16", "Odd256x24",
        "Odd256x32", "Odd256x64", "Odd256x128", "Odd256x256", "Base3x32", "Base7x16", "OnePlus2kx32", "Wide256Square")]
    public string Shape { get; set; } = "MainnetMix";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0xE7E7);
        _b = (UInt256*)NativeMemory.AlignedAlloc((nuint)(Count * sizeof(UInt256)), 64);
        _e = (UInt256*)NativeMemory.AlignedAlloc((nuint)(Count * sizeof(UInt256)), 64);
        for (int i = 0; i < Count; i++)
        {
            (UInt256 b, UInt256 e) = Shape switch
            {
                "MainnetMix" => Mainnet(random),
                "Pow2Mixed" => (new UInt256(random.Next(2) == 0 ? 256UL : 2UL), new UInt256(Pick(random, 20, 28, 16, 2, 160, 176, 224))),
                "Pow2Fixed" => (new UInt256(256), new UInt256(20)),
                "Ten" => (new UInt256(10), new UInt256((ulong)random.Next(1, 37))),
                "NarrowSquare" => (new UInt256(Next64(random), (ulong)random.Next(1 << 30)), new UInt256(2)),
                "Odd256x8" => (Odd256(random), Exponent(random, 8)),
                "Odd256x16" => (Odd256(random), Exponent(random, 16)),
                "Odd256x24" => (Odd256(random), Exponent(random, 24)),
                "Odd256x32" => (Odd256(random), Exponent(random, 32)),
                "Odd256x64" => (Odd256(random), Exponent(random, 64)),
                "Odd256x128" => (Odd256(random), Exponent(random, 128)),
                "Odd256x256" => (Odd256(random), Exponent(random, 256)),
                "Base3x32" => (new UInt256(3), Exponent(random, 32)),
                "Base7x16" => (new UInt256(7), Exponent(random, 16)),
                "OnePlus2kx32" => (new UInt256((1UL << random.Next(8, 61)) + 1), Exponent(random, 32)),
                _ => (Random256(random), new UInt256(2)),
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
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong Exp()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            UInt256.Exp(in _b[i], in _e[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    private static (UInt256, UInt256) Mainnet(Random random)
    {
        int roll = random.Next(1000);
        // Per mille of non-zero-exponent EXP calls with a base other than 0 or 1.
        if (roll < 706) return (new UInt256(256), new UInt256(Pick(random, 20, 20, 20, 20, 20, 20, 28, 28, 16, 2, 1, 6, 31, 32, 21, 8)));
        if (roll < 931) return (new UInt256(2), new UInt256(Pick(random, 160, 160, 160, 160, 160, 2, 176, 224)));
        if (roll < 975) return (new UInt256(10), new UInt256(Pick(random, 18, 18, 10, 6, 12, 8, 36)));
        return random.Next(4) switch
        {
            0 => (new UInt256(1260), new UInt256(1)),
            1 => (new UInt256(1_000_000_000_000_000), new UInt256(2)),
            _ => (new UInt256(Next64(random), (ulong)random.Next(1 << 30)), new UInt256((ulong)random.Next(1, 3))),
        };
    }

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
