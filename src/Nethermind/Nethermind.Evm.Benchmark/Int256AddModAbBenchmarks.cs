// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
public unsafe class Int256AddModAbBenchmarks
{
    private const int Count = 1024;

    private static readonly UInt256 Bn254Q = UInt256.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208583");

    private UInt256* _x;
    private UInt256* _y;
    private UInt256* _m;
    private UInt256* _out;

    [Params("M64Reduced", "M64Small", "M64Wide", "M32Reduced", "M128Reduced", "M192Reduced", "M128", "M192",
        "Bn254Reduced", "NoOverflowMod", "Overflow256")]
    public string Shape { get; set; } = "M64Reduced";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0xADD5);
        _x = Allocate();
        _y = Allocate();
        _m = Allocate();
        _out = Allocate();
        for (int i = 0; i < Count; i++)
        {
            UInt256 m;
            UInt256 x;
            UInt256 y;
            switch (Shape)
            {
                case "M64Reduced":
                    m = Bits(random, 64);
                    x = Bits(random, 64) % m;
                    y = Bits(random, 64) % m;
                    break;
                case "M64Small":
                    m = Bits(random, 60);
                    x = Bits(random, 50) % m;
                    y = Bits(random, 50) % m;
                    break;
                case "M64Wide":
                    m = Bits(random, 64);
                    x = Bits(random, 256);
                    y = Bits(random, 200);
                    break;
                case "M32Reduced":
                    m = Bits(random, 32);
                    x = Bits(random, 64) % m;
                    y = Bits(random, 64) % m;
                    break;
                case "M128Reduced":
                    m = Bits(random, 120);
                    x = Bits(random, 256) % m;
                    y = Bits(random, 256) % m;
                    break;
                case "M192Reduced":
                    m = Bits(random, 190);
                    x = Bits(random, 256) % m;
                    y = Bits(random, 256) % m;
                    break;
                case "M128":
                    m = Bits(random, 120);
                    x = Bits(random, 256);
                    y = Bits(random, 256);
                    break;
                case "M192":
                    m = Bits(random, 180);
                    x = Bits(random, 256);
                    y = Bits(random, 256);
                    break;
                case "Bn254Reduced":
                    m = Bn254Q;
                    x = Bits(random, 256) % m;
                    y = Bits(random, 256) % m;
                    break;
                case "NoOverflowMod":
                    m = Bits(random, 230);
                    x = Bits(random, 250);
                    y = Bits(random, 250);
                    break;
                default:
                    m = Bits(random, 240);
                    x = Bits(random, 256);
                    y = Bits(random, 256);
                    break;
            }

            _x[i] = x;
            _y[i] = y;
            _m[i] = m;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NativeMemory.AlignedFree(_x);
        NativeMemory.AlignedFree(_y);
        NativeMemory.AlignedFree(_m);
        NativeMemory.AlignedFree(_out);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void AddMod()
    {
        for (int i = 0; i < Count; i++) AddMod(in _x[i], in _y[i], in _m[i], ref _out[i]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddMod(in UInt256 x, in UInt256 y, in UInt256 m, ref UInt256 slot)
    {
        UInt256.AddMod(in x, in y, in m, out UInt256 result);
        Unsafe.WriteUnaligned(ref Unsafe.As<UInt256, byte>(ref slot), result);
    }

    private static UInt256* Allocate() => (UInt256*)NativeMemory.AlignedAlloc((nuint)(Count * sizeof(UInt256)), 64);

    private static ulong Next64(Random random) => ((ulong)random.NextInt64() << 1) | (uint)random.Next(2);

    private static UInt256 Bits(Random random, int bits)
    {
        UInt256 x = new(Next64(random), Next64(random), Next64(random), Next64(random));
        x = bits >= 256 ? x : x & ((UInt256.One << bits) - 1);
        return x | (UInt256.One << (bits - 1));
    }
}
