// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

// Calls the referenced Int256 package as shipped, so base and head differ only by the package build.
// Operands live in 64-byte aligned native memory, like the 32-byte aligned EVM stack: a managed array may
// start 16 bytes off, which splits every other 32-byte vector load across cache lines and makes timings
// depend on where the GC placed the array.
[MemoryDiagnoser]
public unsafe class Int256LibraryAbBenchmarks
{
    private const int Count = 1024;

    private static readonly UInt256 Bn254R = UInt256.Parse("21888242871839275222246405745257275088548364400416034343698204186575808495617");
    private static readonly UInt256 Bn254Q = UInt256.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208583");
    private static readonly UInt256 Secp256k1P = UInt256.Parse("115792089237316195423570985008687907853269984665640564039457584007908834671663");

    private UInt256* _x;
    private UInt256* _y;
    private UInt256* _m;

    [Params("Bn254Reduced", "Reduced192Shifted", "RandomShiftedModulus", "Secp256k1Reduced", "SmallOperands",
        "Bn254BelowTwiceModulus", "RandomUnreduced")]
    public string Shape { get; set; } = "Bn254Reduced";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0x1AB2);
        _x = Allocate();
        _y = Allocate();
        _m = Allocate();
        for (int i = 0; i < Count; i++)
        {
            UInt256 m;
            UInt256 x = Random256(random);
            UInt256 y = Random256(random);
            switch (Shape)
            {
                case "Bn254Reduced":
                    m = (i & 1) == 0 ? Bn254R : Bn254Q;
                    x = Below(x, m);
                    y = Below(y, m);
                    break;
                case "Reduced192Shifted":
                    int bits = random.Next(129, 192);
                    m = (Random256(random) & ((UInt256.One << bits) - 1)) | (UInt256.One << (bits - 1));
                    x = Below(x, m);
                    y = Below(y, m);
                    break;
                case "RandomShiftedModulus":
                    m = (Random256(random) >> random.Next(1, 64)) | (UInt256.One << 192);
                    break;
                case "Secp256k1Reduced":
                    m = Secp256k1P;
                    x = Below(x, m);
                    y = Below(y, m);
                    break;
                case "SmallOperands":
                    m = new UInt256(x.u2 | 3);
                    x = new UInt256(x.u0);
                    y = new UInt256(y.u0);
                    break;
                case "Bn254BelowTwiceModulus":
                    m = (i & 1) == 0 ? Bn254R : Bn254Q;
                    x = Below(x, m);
                    y = Below(y, m);
                    if (random.Next(2) == 0) x += m;
                    if (random.Next(2) == 0) y += m;
                    break;
                default:
                    m = Random256(random) | (UInt256.One << 255);
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
    }

    internal static UInt256* Allocate() => (UInt256*)NativeMemory.AlignedAlloc((nuint)(Count * sizeof(UInt256)), 64);

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong MultiplyMod()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            MultiplyMod(in _x[i], in _y[i], in _m[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong AddMod()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            AddMod(in _x[i], in _y[i], in _m[i], out UInt256 r);
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
            Mod(in _x[i], in _m[i], out UInt256 r);
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
            Divide(in _x[i], in _m[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MultiplyMod(in UInt256 x, in UInt256 y, in UInt256 m, out UInt256 res) => UInt256.MultiplyMod(in x, in y, in m, out res);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddMod(in UInt256 x, in UInt256 y, in UInt256 m, out UInt256 res) => UInt256.AddMod(in x, in y, in m, out res);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Mod(in UInt256 x, in UInt256 m, out UInt256 res) => UInt256.Mod(in x, in m, out res);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Divide(in UInt256 x, in UInt256 m, out UInt256 res) => UInt256.Divide(in x, in m, out res);

    private static UInt256 Below(in UInt256 value, in UInt256 m)
    {
        UInt256.Mod(in value, in m, out UInt256 reduced);
        return reduced;
    }

    private static UInt256 Random256(Random random)
    {
        Span<byte> bytes = stackalloc byte[32];
        random.NextBytes(bytes);
        return new UInt256(bytes, isBigEndian: false);
    }
}

// Same reduced BN254 operands at a 32-byte aligned address and at 16 bytes past it, to show how much of an
// AddMod timing comes from where the operands sit rather than from the routine.
[MemoryDiagnoser]
public unsafe class Int256OperandAlignmentBenchmarks
{
    private const int Count = 1024;

    private static readonly UInt256 Bn254R = UInt256.Parse("21888242871839275222246405745257275088548364400416034343698204186575808495617");

    private byte* _block;
    private UInt256* _x;
    private UInt256* _y;
    private UInt256* _m;

    [Params(0, 16)]
    public int Offset { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        nuint bytes = (nuint)(Count * sizeof(UInt256) + 64);
        _block = (byte*)NativeMemory.AlignedAlloc(3 * bytes, 64);
        _x = (UInt256*)(_block + Offset);
        _y = (UInt256*)(_block + bytes + (nuint)Offset);
        _m = (UInt256*)(_block + 2 * bytes + (nuint)Offset);
        Random random = new(0x0A11);
        Span<byte> buffer = stackalloc byte[32];
        for (int i = 0; i < Count; i++)
        {
            random.NextBytes(buffer);
            UInt256.Mod(new UInt256(buffer, isBigEndian: false), Bn254R, out _x[i]);
            random.NextBytes(buffer);
            UInt256.Mod(new UInt256(buffer, isBigEndian: false), Bn254R, out _y[i]);
            _m[i] = Bn254R;
        }
    }

    [GlobalCleanup]
    public void Cleanup() => NativeMemory.AlignedFree(_block);

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong AddMod()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            AddModCall(in _x[i], in _y[i], in _m[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong MultiplyMod()
    {
        ulong acc = 0;
        for (int i = 0; i < Count; i++)
        {
            MultiplyModCall(in _x[i], in _y[i], in _m[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddModCall(in UInt256 x, in UInt256 y, in UInt256 m, out UInt256 res) => UInt256.AddMod(in x, in y, in m, out res);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MultiplyModCall(in UInt256 x, in UInt256 y, in UInt256 m, out UInt256 res) => UInt256.MultiplyMod(in x, in y, in m, out res);
}
