// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

// MOD and DIV by field moduli and by divisors of each width, calling the referenced Int256 package as shipped, so base
// and head differ only by the package build. Operands in 64-byte aligned native memory as in Int256LibraryAbBenchmarks.
[MemoryDiagnoser]
public unsafe class Int256ModReductionAbBenchmarks
{
    private const int Count = 1024;

    private static readonly UInt256 Bn254R = UInt256.Parse("21888242871839275222246405745257275088548364400416034343698204186575808495617");
    private static readonly UInt256 Bn254P = UInt256.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208583");
    private static readonly UInt256 Wad = 1_000_000_000_000_000_000UL;

    private UInt256* _x;
    private UInt256* _y;

    // Full256: a 256-bit dividend. By64Wide: 33..64-bit divisor, 129..256-bit dividend. Wad192: 10^18 into 129..192
    // bits. By128Wide: 65..128-bit divisor, 129..256-bit dividend. Full256By256: 193..255-bit divisor of another top.
    [Params("Bn254RFull256", "Bn254PFull256", "TopLimbRandom", "Full256By256", "By64Wide", "Wad192", "By128Wide")]
    public string Shape { get; set; } = "Bn254RFull256";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0x3D0D);
        _x = Int256LibraryAbBenchmarks.Allocate();
        _y = Int256LibraryAbBenchmarks.Allocate();
        for (int i = 0; i < Count; i++)
        {
            UInt256 x;
            UInt256 y;
            switch (Shape)
            {
                case "Bn254RFull256":
                    x = RandomBits(random, 256);
                    y = Bn254R;
                    break;
                case "Bn254PFull256":
                    x = RandomBits(random, 256);
                    y = Bn254P;
                    break;
                case "TopLimbRandom":
                    x = RandomBits(random, 256);
                    y = new UInt256(RandomBits(random, 64).u0, RandomBits(random, 64).u0, RandomBits(random, 64).u0, Bn254R.u3);
                    break;
                case "Full256By256":
                    x = RandomBits(random, 256);
                    y = RandomBits(random, random.Next(193, 256));
                    break;
                case "By64Wide":
                    x = RandomBits(random, random.Next(129, 257));
                    y = RandomBits(random, random.Next(33, 65));
                    break;
                case "Wad192":
                    x = RandomBits(random, random.Next(129, 193));
                    y = Wad;
                    break;
                default:
                    x = RandomBits(random, random.Next(129, 257));
                    y = RandomBits(random, random.Next(65, 129));
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
    public ulong Mod()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            Mod(in _x[i], in _y[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong Divide()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            Divide(in _x[i], in _y[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Mod(in UInt256 x, in UInt256 y, out UInt256 res) => UInt256.Mod(in x, in y, out res);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Divide(in UInt256 x, in UInt256 y, out UInt256 res) => UInt256.Divide(in x, in y, out res);

    // A random value of exactly the given width: the top bit is set, the rest are random.
    private static UInt256 RandomBits(Random random, int bits)
    {
        Span<byte> bytes = stackalloc byte[32];
        random.NextBytes(bytes);
        UInt256 value = new(bytes, isBigEndian: false);
        if (bits < 256)
        {
            value &= (UInt256.One << bits) - UInt256.One;
        }
        return value | (UInt256.One << (bits - 1));
    }
}
