// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

// DIV and MOD operand shapes by divisor width, each with a dividend that is shorter than four limbs and one
// that is not. Calls the referenced Int256 package as shipped, with operands in 64-byte aligned native memory
// as in Int256LibraryAbBenchmarks.
[MemoryDiagnoser]
public unsafe class Int256DivideShapesBenchmarks
{
    private const int Count = 1024;

    private static readonly UInt256 Wad = 1_000_000_000_000_000_000UL;

    private UInt256* _x;
    private UInt256* _y;

    [Params("WadScaled", "WadWide", "Full256By64", "ShortBy128", "MulDivBy128", "Full256By128", "MulDivBy192",
        "Full256By192")]
    public string Shape { get; set; } = "WadScaled";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0xD1A5);
        _x = Int256LibraryAbBenchmarks.Allocate();
        _y = Int256LibraryAbBenchmarks.Allocate();
        for (int i = 0; i < Count; i++)
        {
            UInt256 x;
            UInt256 y;
            int divisorBits;
            switch (Shape)
            {
                case "WadScaled":
                    // An amount of up to 64 bits scaled by 1e18, divided by 1e18 again.
                    y = Wad;
                    UInt256.Multiply(RandomBits(random, random.Next(20, 65)), Wad, out x);
                    break;
                case "WadWide":
                    y = Wad;
                    x = RandomBits(random, random.Next(129, 251));
                    break;
                case "Full256By64":
                    y = RandomBits(random, random.Next(33, 65)) | UInt256.One;
                    x = RandomBits(random, 256);
                    break;
                case "ShortBy128":
                    divisorBits = random.Next(65, 121);
                    y = RandomBits(random, divisorBits);
                    x = RandomBits(random, random.Next(divisorBits + 1, 129));
                    break;
                case "MulDivBy128":
                    y = RandomBits(random, random.Next(65, 129));
                    x = RandomBits(random, random.Next(129, 193));
                    break;
                case "Full256By128":
                    y = RandomBits(random, random.Next(65, 129));
                    x = RandomBits(random, 256);
                    break;
                case "MulDivBy192":
                    divisorBits = random.Next(129, 181);
                    y = RandomBits(random, divisorBits);
                    x = RandomBits(random, random.Next(divisorBits + 1, 193));
                    break;
                default:
                    y = RandomBits(random, random.Next(129, 193));
                    x = RandomBits(random, 256);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Divide(in UInt256 x, in UInt256 y, out UInt256 res) => UInt256.Divide(in x, in y, out res);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Mod(in UInt256 x, in UInt256 y, out UInt256 res) => UInt256.Mod(in x, in y, out res);

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
