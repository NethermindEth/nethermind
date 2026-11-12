// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

// DIV, MOD, ADDMOD, MULMOD and EXP as the opcode handlers call them: operands read in place, the result written to
// a local and then copied on as one 32-byte word, the way the handler writes it to its stack slot. Base and head
// differ only by the referenced Int256 package.
[MemoryDiagnoser]
public unsafe class Int256DeepAbBenchmarks
{
    private const int Count = 2048;

    private static readonly UInt256 Wad = new(1_000_000_000_000_000_000UL);
    private static readonly UInt256 FieldR = UInt256.Parse("21888242871839275222246405745257275088548364400416034343698204186575808495617");

    private UInt256* _x;
    private UInt256* _y;
    private UInt256* _m;
    private UInt256* _out;

    private UInt256* _divWadX;
    private UInt256* _divWide64Y;
    private UInt256* _div128Y;
    private UInt256* _div192Y;
    private UInt256* _div256X;
    private UInt256* _div256Y;
    private UInt256* _divPow2Y;
    private UInt256* _smallX;
    private UInt256* _smallY;
    private UInt256* _fieldX;
    private UInt256* _fieldY;
    private UInt256* _expBase;
    private UInt256* _expShort;
    private UInt256* _two;
    private UInt256* _expPow2;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0x1D17);
        _x = Allocate();
        _y = Allocate();
        _m = Allocate();
        _out = Allocate();
        _divWadX = Allocate();
        _divWide64Y = Allocate();
        _div128Y = Allocate();
        _div192Y = Allocate();
        _div256X = Allocate();
        _div256Y = Allocate();
        _divPow2Y = Allocate();
        _smallX = Allocate();
        _smallY = Allocate();
        _fieldX = Allocate();
        _fieldY = Allocate();
        _expBase = Allocate();
        _expShort = Allocate();
        _two = Allocate();
        _expPow2 = Allocate();
        int[] powers = [32, 64, 96, 112, 128, 160, 192, 224];
        for (int i = 0; i < Count; i++)
        {
            _x[i] = Random256(random);
            _y[i] = Random256(random) >> random.Next(1, 200);
            _m[i] = Random256(random) | new UInt256(1, 0, 0, 1UL << 62);
            _divWadX[i] = Random256(random) >> random.Next(126, 170);
            _divWide64Y[i] = new UInt256((ulong)random.NextInt64(2, long.MaxValue) >> random.Next(0, 40));
            _div128Y[i] = (Random256(random) >> random.Next(128, 192)) | new UInt256(0, 1);
            _div192Y[i] = (Random256(random) >> random.Next(64, 128)) | new UInt256(0, 0, 1);
            _div256Y[i] = (Random256(random) >> random.Next(1, 63)) | new UInt256(0, 0, 0, 1);
            _div256X[i] = _div256Y[i] + (Random256(random) >> random.Next(1, 64));
            _divPow2Y[i] = UInt256.One << powers[random.Next(powers.Length)];
            _smallX[i] = new UInt256((ulong)random.NextInt64());
            _smallY[i] = new UInt256((ulong)random.NextInt64(1, 1L << 40));
            UInt256.Mod(Random256(random), FieldR, out _fieldX[i]);
            UInt256.Mod(Random256(random), FieldR, out _fieldY[i]);
            _expBase[i] = new UInt256((ulong)(random.Next(1, 5000) * 2 + 1));
            _expShort[i] = new UInt256((ulong)random.Next(2, 1 << 16));
            _two[i] = new UInt256(2);
            _expPow2[i] = new UInt256((ulong)random.Next(2, 256));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NativeMemory.AlignedFree(_x);
        NativeMemory.AlignedFree(_y);
        NativeMemory.AlignedFree(_m);
        NativeMemory.AlignedFree(_out);
        NativeMemory.AlignedFree(_divWadX);
        NativeMemory.AlignedFree(_divWide64Y);
        NativeMemory.AlignedFree(_div128Y);
        NativeMemory.AlignedFree(_div192Y);
        NativeMemory.AlignedFree(_div256X);
        NativeMemory.AlignedFree(_div256Y);
        NativeMemory.AlignedFree(_divPow2Y);
        NativeMemory.AlignedFree(_smallX);
        NativeMemory.AlignedFree(_smallY);
        NativeMemory.AlignedFree(_fieldX);
        NativeMemory.AlignedFree(_fieldY);
        NativeMemory.AlignedFree(_expBase);
        NativeMemory.AlignedFree(_expShort);
        NativeMemory.AlignedFree(_two);
        NativeMemory.AlignedFree(_expPow2);
    }

    // x / 10^18 with x of 2 to 3 limbs: fixed-point WAD math.
    [Benchmark(OperationsPerInvoke = Count)]
    public void DivWad()
    {
        for (int i = 0; i < Count; i++) Div(in _divWadX[i], in Wad, ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void DivWide64()
    {
        for (int i = 0; i < Count; i++) Div(in _x[i], in _divWide64Y[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void Div128()
    {
        for (int i = 0; i < Count; i++) Div(in _x[i], in _div128Y[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void Div192()
    {
        for (int i = 0; i < Count; i++) Div(in _x[i], in _div192Y[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void Div256()
    {
        for (int i = 0; i < Count; i++) Div(in _div256X[i], in _div256Y[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void DivPow2()
    {
        for (int i = 0; i < Count; i++) Div(in _x[i], in _divPow2Y[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void DivSmall()
    {
        for (int i = 0; i < Count; i++) Div(in _smallX[i], in _smallY[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void DivMixed()
    {
        for (int i = 0; i < Count; i++) Div(in _x[i], in _y[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void ModWad()
    {
        for (int i = 0; i < Count; i++) Mod(in _divWadX[i], in Wad, ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void ModMixed()
    {
        for (int i = 0; i < Count; i++) Mod(in _x[i], in _y[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void ModPow2()
    {
        for (int i = 0; i < Count; i++) Mod(in _x[i], in _divPow2Y[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void ModSmall()
    {
        for (int i = 0; i < Count; i++) Mod(in _smallX[i], in _smallY[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void AddModField()
    {
        for (int i = 0; i < Count; i++) AddMod(in _fieldX[i], in _fieldY[i], in FieldR, ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void AddModRandom()
    {
        for (int i = 0; i < Count; i++) AddMod(in _x[i], in _y[i], in _m[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void MulModField()
    {
        for (int i = 0; i < Count; i++) MulMod(in _fieldX[i], in _fieldY[i], in FieldR, ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void MulModRandom()
    {
        for (int i = 0; i < Count; i++) MulMod(in _x[i], in _y[i], in _m[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void ExpShort()
    {
        for (int i = 0; i < Count; i++) Exp(in _expBase[i], in _expShort[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void ExpPow2()
    {
        for (int i = 0; i < Count; i++) Exp(in _two[i], in _expPow2[i], ref _out[i]);
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public void ExpWide()
    {
        for (int i = 0; i < Count; i++) Exp(in _x[i], in _y[i], ref _out[i]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Div(in UInt256 a, in UInt256 b, ref UInt256 slot)
    {
        UInt256 result;
        if (b.IsZero) result = default;
        else UInt256.Divide(in a, in b, out result);
        Unsafe.WriteUnaligned(ref Unsafe.As<UInt256, byte>(ref slot), result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Mod(in UInt256 a, in UInt256 b, ref UInt256 slot)
    {
        UInt256 result;
        if (b.IsZeroOrOne) result = default;
        else UInt256.Mod(in a, in b, out result);
        Unsafe.WriteUnaligned(ref Unsafe.As<UInt256, byte>(ref slot), result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddMod(in UInt256 a, in UInt256 b, in UInt256 m, ref UInt256 slot)
    {
        UInt256 c = m;
        UInt256.AddMod(in a, in b, in c, out slot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MulMod(in UInt256 a, in UInt256 b, in UInt256 m, ref UInt256 slot)
    {
        UInt256 c = m;
        UInt256.MultiplyMod(in a, in b, in c, out slot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Exp(in UInt256 b, in UInt256 e, ref UInt256 slot)
    {
        UInt256.Exp(in b, in e, out UInt256 result);
        Unsafe.WriteUnaligned(ref Unsafe.As<UInt256, byte>(ref slot), result);
    }

    private static UInt256* Allocate() => (UInt256*)NativeMemory.AlignedAlloc((nuint)(Count * sizeof(UInt256)), 64);

    private static UInt256 Random256(Random random)
    {
        Span<byte> bytes = stackalloc byte[32];
        random.NextBytes(bytes);
        return new UInt256(bytes, isBigEndian: false);
    }
}
