// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#pragma warning disable SYSLIB5004 // The copied kernels use the experimental X86Base.X64.DivRem like the library.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

// In-process A/B for 256-bit arithmetic kernels behind MULMOD, ADDMOD, SDIV and SMOD: each pair runs the
// current routine and a candidate over the same inputs in one process, so their ratio is comparable.
internal static class Int256AbInputs
{
    public const int Count = 1024;

    public static readonly UInt256 Bn254R = UInt256.Parse("21888242871839275222246405745257275088548364400416034343698204186575808495617");
    public static readonly UInt256 Bn254Q = UInt256.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208583");
    public static readonly UInt256 Secp256k1P = UInt256.Parse("115792089237316195423570985008687907853269984665640564039457584007908834671663");

    public static UInt256 Random256(Random random)
    {
        Span<byte> bytes = stackalloc byte[32];
        random.NextBytes(bytes);
        return new UInt256(bytes, isBigEndian: false);
    }

    public static UInt256 RandomBits(Random random, int bits)
    {
        UInt256 value = Random256(random);
        return bits >= 256 ? value : value & ((UInt256.One << bits) - 1);
    }

    public static UInt256 RandomBelow(Random random, in UInt256 bound)
    {
        UInt256.Mod(Random256(random), bound, out UInt256 value);
        return value;
    }

    // EVM words: mostly one or two limbs, some full width.
    public static UInt256 RandomWord(Random random)
    {
        int pick = random.Next(10);
        int bits = pick < 6 ? random.Next(1, 65) : pick < 9 ? random.Next(65, 129) : random.Next(129, 257);
        UInt256 value = RandomBits(random, bits);
        return value.IsZero ? UInt256.One : value;
    }
}

[MemoryDiagnoser]
public class Int256MulModReductionAbBenchmarks
{
    private UInt256[] _x = null!;
    private UInt256[] _y = null!;
    private UInt256[] _m = null!;
    private UInt256[] _lo = null!;
    private UInt256[] _hi = null!;

    [Params("Bn254Reduced", "Secp256k1Reduced", "RandomOperandsShiftedModulus", "Reduced192ShiftedModulus")]
    public string Shape { get; set; } = "Bn254Reduced";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0x4D4D);
        int count = Int256AbInputs.Count;
        _x = new UInt256[count];
        _y = new UInt256[count];
        _m = new UInt256[count];
        _lo = new UInt256[count];
        _hi = new UInt256[count];
        for (int i = 0; i < count; i++)
        {
            UInt256 m;
            UInt256 x;
            UInt256 y;
            switch (Shape)
            {
                case "Bn254Reduced":
                    m = (i & 1) == 0 ? Int256AbInputs.Bn254R : Int256AbInputs.Bn254Q;
                    x = Int256AbInputs.RandomBelow(random, m);
                    y = Int256AbInputs.RandomBelow(random, m);
                    break;
                case "Secp256k1Reduced":
                    m = Int256AbInputs.Secp256k1P;
                    x = Int256AbInputs.RandomBelow(random, m);
                    y = Int256AbInputs.RandomBelow(random, m);
                    break;
                case "RandomOperandsShiftedModulus":
                    m = Int256AbInputs.RandomBits(random, 256 - random.Next(1, 64)) | (UInt256.One << 192);
                    x = Int256AbInputs.Random256(random);
                    y = Int256AbInputs.Random256(random);
                    break;
                default:
                    int bits = random.Next(129, 192);
                    m = Int256AbInputs.RandomBits(random, bits) | (UInt256.One << (bits - 1));
                    x = Int256AbInputs.RandomBelow(random, m);
                    y = Int256AbInputs.RandomBelow(random, m);
                    break;
            }

            _x[i] = x;
            _y[i] = y;
            _m[i] = m;
            Product(x, y, out _lo[i], out _hi[i]);
            if (_hi[i].IsZero) _hi[i] = UInt256.One;
        }

        for (int i = 0; i < count; i++)
        {
            ReduceCurrent(in _lo[i], in _hi[i], in _m[i], out UInt256 a);
            ReduceShiftedSkip(in _lo[i], in _hi[i], in _m[i], out UInt256 b);
            BigInteger expected = (((BigInteger)_hi[i] << 256) + (BigInteger)_lo[i]) % (BigInteger)_m[i];
            if ((BigInteger)a != expected || (BigInteger)b != expected) throw new InvalidOperationException($"Reduction mismatch at {i}");
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Int256AbInputs.Count)]
    public ulong ReductionCurrent()
    {
        ulong acc = 0;
        for (int i = 0; i < _m.Length; i++)
        {
            ReduceCurrent(in _lo[i], in _hi[i], in _m[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Int256AbInputs.Count)]
    public ulong ReductionShiftedSkip()
    {
        ulong acc = 0;
        for (int i = 0; i < _m.Length; i++)
        {
            ReduceShiftedSkip(in _lo[i], in _hi[i], in _m[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    // Whole library MultiplyMod on the same operands, to size the reduction within the opcode.
    [Benchmark(OperationsPerInvoke = Int256AbInputs.Count)]
    public ulong LibraryMultiplyMod()
    {
        ulong acc = 0;
        for (int i = 0; i < _m.Length; i++)
        {
            LibraryMulMod(in _x[i], in _y[i], in _m[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LibraryMulMod(in UInt256 x, in UInt256 y, in UInt256 m, out UInt256 res)
        => UInt256.MultiplyMod(in x, in y, in m, out res);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReduceCurrent(in UInt256 lo, in UInt256 hi, in UInt256 m, out UInt256 res)
    {
        if (m.u3 != 0) Reducer<NoSkip>.By256(in lo, in hi, in m, out res);
        else Reducer<NoSkip>.By192(in lo, in hi, in m, out res);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReduceShiftedSkip(in UInt256 lo, in UInt256 hi, in UInt256 m, out UInt256 res)
    {
        if (m.u3 != 0) Reducer<Skip>.By256(in lo, in hi, in m, out res);
        else Reducer<Skip>.By192(in lo, in hi, in m, out res);
    }

    private static void Product(in UInt256 x, in UInt256 y, out UInt256 lo, out UInt256 hi)
    {
        BigInteger p = (BigInteger)x * (BigInteger)y;
        lo = (UInt256)(p & ((BigInteger.One << 256) - 1));
        hi = (UInt256)(p >> 256);
    }

    private interface ISkipFlag
    {
        static abstract bool IsActive { get; }
    }

    private readonly struct NoSkip : ISkipFlag
    {
        public static bool IsActive => false;
    }

    private readonly struct Skip : ISkipFlag
    {
        public static bool IsActive => true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ULong9
    {
        public ulong w0, w1, w2, w3, w4, w5, w6, w7, w8;
    }

    // Copies of the library's 512-by-256 and 512-by-192 remainder kernels; TSkip adds only the zero
    // leading quotient digit test in the normalising-shift branch.
    private static class Reducer<TSkip> where TSkip : struct, ISkipFlag
    {
        [SkipLocalsInit]
        public static void By256(in UInt256 lo, in UInt256 hi, in UInt256 d, out UInt256 rem)
        {
            Unsafe.SkipInit(out rem);
            ulong d0 = d.u0, d1 = d.u1, d2 = d.u2, d3 = d.u3;
            int shift = BitOperations.LeadingZeroCount(d3);
            ulong u4 = hi.u0, u5 = hi.u1, u6 = hi.u2, u7 = hi.u3;
            int uLen = u7 != 0 ? 8 : (u6 != 0 ? 7 : (u5 != 0 ? 6 : 5));

            Unsafe.SkipInit(out ULong9 unBuf);
            ulong nd0, nd1, nd2, nd3;

            if (shift == 0)
            {
                unBuf.w0 = lo.u0; unBuf.w1 = lo.u1; unBuf.w2 = lo.u2; unBuf.w3 = lo.u3;
                unBuf.w4 = u4; unBuf.w5 = u5; unBuf.w6 = u6; unBuf.w7 = u7;
                unBuf.w8 = 0;
                if (Unsafe.Add(ref unBuf.w0, uLen - 1) < d3) uLen--;
                nd0 = d0; nd1 = d1; nd2 = d2; nd3 = d3;
            }
            else
            {
                ulong u0 = lo.u0, u1n = lo.u1, u2n = lo.u2, u3n = lo.u3;
                int rshift = 64 - shift;

                unBuf.w8 = u7 >> rshift;
                unBuf.w7 = (u7 << shift) | (u6 >> rshift);
                unBuf.w6 = (u6 << shift) | (u5 >> rshift);
                unBuf.w5 = (u5 << shift) | (u4 >> rshift);
                unBuf.w4 = (u4 << shift) | (u3n >> rshift);
                unBuf.w3 = (u3n << shift) | (u2n >> rshift);
                unBuf.w2 = (u2n << shift) | (u1n >> rshift);
                unBuf.w1 = (u1n << shift) | (u0 >> rshift);
                unBuf.w0 = u0 << shift;

                nd0 = d0 << shift;
                nd1 = (d1 << shift) | (d0 >> rshift);
                nd2 = (d2 << shift) | (d1 >> rshift);
                nd3 = (d3 << shift) | (d2 >> rshift);

                if (TSkip.IsActive && Unsafe.Add(ref unBuf.w0, uLen) == 0 && Unsafe.Add(ref unBuf.w0, uLen - 1) < nd3) uLen--;
            }

            ref ulong un0 = ref unBuf.w0;
            int m = uLen - 4;
            ulong reciprocal = X86Base.X64.IsSupported ? 0 : Reciprocal2By1(nd3);

            for (int j = m; j >= 0; j--)
            {
                ref ulong uJ = ref Unsafe.Add(ref un0, j);

                ulong u8 = Unsafe.Add(ref uJ, 4);
                ulong u9 = Unsafe.Add(ref uJ, 3);
                ulong u10 = Unsafe.Add(ref uJ, 2);

                ulong qhat;
                {
                    if (u8 >= nd3)
                    {
                        qhat = EstimateQhat(u8, u9, u10, nd3, nd2, reciprocal);
                        goto FullSubMul;
                    }

                    ulong rhat;
                    if (X86Base.X64.IsSupported)
                    {
                        (qhat, rhat) = X86Base.X64.DivRem(u9, u8, nd3);
                    }
                    else
                    {
                        qhat = UDivRem2By1(u8, reciprocal, nd3, u9, out rhat);
                    }

                    ulong ph = Multiply64(qhat, nd2, out ulong pl);
                    while (ph > rhat || (ph == rhat && pl > u10))
                    {
                        qhat--;

                        ulong prev = rhat;
                        rhat += nd3;
                        if (rhat < prev) goto FullSubMul;

                        ph -= (pl < nd2) ? 1UL : 0UL;
                        pl -= nd2;
                    }

                    ulong v0 = uJ;
                    ulong hi0 = Multiply64(nd0, qhat, out ulong lo0);
                    uJ = v0 - lo0;
                    ulong borrow2 = hi0 + (lo0 > v0 ? 1UL : 0UL);

                    ref ulong x1 = ref Unsafe.Add(ref uJ, 1);
                    ulong v1 = x1;
                    ulong hi1 = Multiply64(nd1, qhat, out ulong lo1);
                    ulong t1 = v1 - borrow2;
                    x1 = t1 - lo1;
                    borrow2 = hi1 + (borrow2 > v1 ? 1UL : 0UL) + (lo1 > t1 ? 1UL : 0UL);

                    ref ulong x2 = ref Unsafe.Add(ref uJ, 2);
                    ulong v2 = x2;
                    ulong hi2, lo2;
                    if (Bmi2.X64.IsSupported || ArmBase.Arm64.IsSupported)
                    {
                        hi2 = Multiply64(nd2, qhat, out lo2);
                    }
                    else
                    {
                        hi2 = ph;
                        lo2 = pl;
                    }
                    ulong t2 = v2 - borrow2;
                    x2 = t2 - lo2;
                    borrow2 = hi2 + (borrow2 > v2 ? 1UL : 0UL) + (lo2 > t2 ? 1UL : 0UL);

                    Unsafe.Add(ref uJ, 3) = rhat - borrow2;

                    if (borrow2 > rhat)
                    {
                        AddTo4(ref uJ, nd0, nd1, nd2, nd3);
                    }

                    continue;
                }

            FullSubMul:
                ulong borrow = SubMulTo4(ref uJ, nd0, nd1, nd2, nd3, qhat);

                if (u8 < borrow)
                {
                    AddTo4(ref uJ, nd0, nd1, nd2, nd3);
                }
            }

            ulong rw0 = unBuf.w0, rw1 = unBuf.w1, rw2 = unBuf.w2, rw3 = unBuf.w3;

            if (shift == 0)
            {
                Unsafe.AsRef(in rem.u0) = rw0;
                Unsafe.AsRef(in rem.u1) = rw1;
                Unsafe.AsRef(in rem.u2) = rw2;
                Unsafe.AsRef(in rem.u3) = rw3;
            }
            else
            {
                int rshift = 64 - shift;
                Unsafe.AsRef(in rem.u0) = (rw0 >> shift) | (rw1 << rshift);
                Unsafe.AsRef(in rem.u1) = (rw1 >> shift) | (rw2 << rshift);
                Unsafe.AsRef(in rem.u2) = (rw2 >> shift) | (rw3 << rshift);
                Unsafe.AsRef(in rem.u3) = rw3 >> shift;
            }
        }

        [SkipLocalsInit]
        public static void By192(in UInt256 lo, in UInt256 hi, in UInt256 d, out UInt256 rem)
        {
            Unsafe.SkipInit(out rem);
            ulong d0 = d.u0, d1 = d.u1, d2 = d.u2;
            int shift = BitOperations.LeadingZeroCount(d2);
            ulong u4 = hi.u0, u5 = hi.u1, u6 = hi.u2, u7 = hi.u3;
            int uLen = u7 != 0 ? 8 : (u6 != 0 ? 7 : (u5 != 0 ? 6 : 5));

            Unsafe.SkipInit(out ULong9 unBuf);
            ulong nd0, nd1, nd2;

            if (shift == 0)
            {
                unBuf.w0 = lo.u0; unBuf.w1 = lo.u1; unBuf.w2 = lo.u2; unBuf.w3 = lo.u3;
                unBuf.w4 = u4; unBuf.w5 = u5; unBuf.w6 = u6; unBuf.w7 = u7;
                unBuf.w8 = 0;
                if (Unsafe.Add(ref unBuf.w0, uLen - 1) < d2) uLen--;
                nd0 = d0; nd1 = d1; nd2 = d2;
            }
            else
            {
                ulong u0 = lo.u0, u1n = lo.u1, u2n = lo.u2, u3n = lo.u3;
                int rshift = 64 - shift;
                unBuf.w8 = u7 >> rshift;
                unBuf.w7 = (u7 << shift) | (u6 >> rshift);
                unBuf.w6 = (u6 << shift) | (u5 >> rshift);
                unBuf.w5 = (u5 << shift) | (u4 >> rshift);
                unBuf.w4 = (u4 << shift) | (u3n >> rshift);
                unBuf.w3 = (u3n << shift) | (u2n >> rshift);
                unBuf.w2 = (u2n << shift) | (u1n >> rshift);
                unBuf.w1 = (u1n << shift) | (u0 >> rshift);
                unBuf.w0 = u0 << shift;
                nd0 = d0 << shift;
                nd1 = (d1 << shift) | (d0 >> rshift);
                nd2 = (d2 << shift) | (d1 >> rshift);

                if (TSkip.IsActive && Unsafe.Add(ref unBuf.w0, uLen) == 0 && Unsafe.Add(ref unBuf.w0, uLen - 1) < nd2) uLen--;
            }

            ref ulong un0 = ref unBuf.w0;
            int m = uLen - 3;
            ulong reciprocal = X86Base.X64.IsSupported ? 0 : Reciprocal2By1(nd2);

            for (int j = m; j >= 0; j--)
            {
                ref ulong uJ = ref Unsafe.Add(ref un0, j);

                ulong u8 = Unsafe.Add(ref uJ, 3);
                ulong u9 = Unsafe.Add(ref uJ, 2);
                ulong u10 = Unsafe.Add(ref uJ, 1);

                ulong qhat;
                {
                    if (u8 >= nd2)
                    {
                        qhat = EstimateQhat(u8, u9, u10, nd2, nd1, reciprocal);
                        goto FullSubMul;
                    }

                    ulong rhat;
                    if (X86Base.X64.IsSupported)
                    {
                        (qhat, rhat) = X86Base.X64.DivRem(u9, u8, nd2);
                    }
                    else
                    {
                        qhat = UDivRem2By1(u8, reciprocal, nd2, u9, out rhat);
                    }

                    ulong ph = Multiply64(qhat, nd1, out ulong pl);
                    while (ph > rhat || (ph == rhat && pl > u10))
                    {
                        qhat--;

                        ulong prev = rhat;
                        rhat += nd2;
                        if (rhat < prev) goto FullSubMul;

                        ph -= (pl < nd1) ? 1UL : 0UL;
                        pl -= nd1;
                    }

                    ulong v0 = uJ;
                    ulong hi0 = Multiply64(nd0, qhat, out ulong lo0);
                    uJ = v0 - lo0;
                    ulong borrow1 = hi0 + (lo0 > v0 ? 1UL : 0UL);

                    ref ulong x1 = ref Unsafe.Add(ref uJ, 1);
                    ulong v1 = u10;
                    ulong hi1 = ph, lo1 = pl;
                    ulong t1 = v1 - borrow1;
                    x1 = t1 - lo1;
                    borrow1 = hi1 + (borrow1 > v1 ? 1UL : 0UL) + (lo1 > t1 ? 1UL : 0UL);

                    Unsafe.Add(ref uJ, 2) = rhat - borrow1;

                    if (borrow1 > rhat)
                    {
                        AddTo3(ref uJ, nd0, nd1, nd2);
                    }

                    continue;
                }

            FullSubMul:
                ulong borrow = SubMulTo3(ref uJ, nd0, nd1, nd2, qhat);

                if (u8 < borrow)
                {
                    AddTo3(ref uJ, nd0, nd1, nd2);
                }
            }

            ulong rw0 = unBuf.w0, rw1 = unBuf.w1, rw2 = unBuf.w2;

            if (shift == 0)
            {
                Unsafe.AsRef(in rem.u0) = rw0;
                Unsafe.AsRef(in rem.u1) = rw1;
                Unsafe.AsRef(in rem.u2) = rw2;
                Unsafe.AsRef(in rem.u3) = 0;
            }
            else
            {
                int rshift = 64 - shift;
                Unsafe.AsRef(in rem.u0) = (rw0 >> shift) | (rw1 << rshift);
                Unsafe.AsRef(in rem.u1) = (rw1 >> shift) | (rw2 << rshift);
                Unsafe.AsRef(in rem.u2) = rw2 >> shift;
                Unsafe.AsRef(in rem.u3) = 0;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SubMulTo4(ref ulong x0, ulong y0, ulong y1, ulong y2, ulong y3, ulong mul)
    {
        ulong v0 = x0;
        ulong hi0 = Multiply64(y0, mul, out ulong lo0);
        x0 = v0 - lo0;
        ulong borrow = hi0 + (lo0 > v0 ? 1UL : 0UL);

        ref ulong x1 = ref Unsafe.Add(ref x0, 1);
        ulong v1 = x1;
        ulong hi1 = Multiply64(y1, mul, out ulong lo1);
        ulong t1 = v1 - borrow;
        x1 = t1 - lo1;
        borrow = hi1 + (borrow > v1 ? 1UL : 0UL) + (lo1 > t1 ? 1UL : 0UL);

        ref ulong x2 = ref Unsafe.Add(ref x0, 2);
        ulong v2 = x2;
        ulong hi2 = Multiply64(y2, mul, out ulong lo2);
        ulong t2 = v2 - borrow;
        x2 = t2 - lo2;
        borrow = hi2 + (borrow > v2 ? 1UL : 0UL) + (lo2 > t2 ? 1UL : 0UL);

        ref ulong x3 = ref Unsafe.Add(ref x0, 3);
        ulong v3 = x3;
        ulong hi3 = Multiply64(y3, mul, out ulong lo3);
        ulong t3 = v3 - borrow;
        x3 = t3 - lo3;
        return hi3 + (borrow > v3 ? 1UL : 0UL) + (lo3 > t3 ? 1UL : 0UL);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong AddTo4(ref ulong x0, ulong y0, ulong y1, ulong y2, ulong y3)
    {
        ulong a0 = x0;
        ulong a1 = Unsafe.Add(ref x0, 1);
        ulong a2 = Unsafe.Add(ref x0, 2);
        ulong a3 = Unsafe.Add(ref x0, 3);

        ulong s0 = a0 + y0;
        ulong c = s0 < a0 ? 1UL : 0UL;
        ulong t1 = a1 + y1;
        ulong s1 = t1 + c;
        c = (t1 < a1 ? 1UL : 0UL) | (s1 < c ? 1UL : 0UL);
        ulong t2 = a2 + y2;
        ulong s2 = t2 + c;
        c = (t2 < a2 ? 1UL : 0UL) | (s2 < c ? 1UL : 0UL);
        ulong t3 = a3 + y3;
        ulong s3 = t3 + c;
        c = (t3 < a3 ? 1UL : 0UL) | (s3 < c ? 1UL : 0UL);

        x0 = s0;
        Unsafe.Add(ref x0, 1) = s1;
        Unsafe.Add(ref x0, 2) = s2;
        Unsafe.Add(ref x0, 3) = s3;
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SubMulTo3(ref ulong x0, ulong y0, ulong y1, ulong y2, ulong mul)
    {
        ref ulong x1 = ref Unsafe.Add(ref x0, 1);
        ref ulong x2 = ref Unsafe.Add(ref x0, 2);

        ulong ph0 = Multiply64(y0, mul, out ulong pl0);
        ulong v0 = x0;
        x0 = v0 - pl0;
        ulong borrow = ph0 + (v0 < pl0 ? 1UL : 0UL);

        ulong ph1 = Multiply64(y1, mul, out ulong pl1);
        ulong v1 = x1;
        ulong sum1 = pl1 + borrow;
        ulong c1 = sum1 < borrow ? 1UL : 0UL;
        x1 = v1 - sum1;
        borrow = ph1 + c1 + (v1 < sum1 ? 1UL : 0UL);

        ulong ph2 = Multiply64(y2, mul, out ulong pl2);
        ulong v2 = x2;
        ulong sum2 = pl2 + borrow;
        ulong c2 = sum2 < borrow ? 1UL : 0UL;
        x2 = v2 - sum2;
        borrow = ph2 + c2 + (v2 < sum2 ? 1UL : 0UL);

        return borrow;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong AddTo3(ref ulong x0, ulong y0, ulong y1, ulong y2)
    {
        ulong a = x0;
        ulong b = Unsafe.Add(ref x0, 1);
        ulong c = Unsafe.Add(ref x0, 2);

        ulong s0 = a + y0;
        ulong c0 = s0 < a ? 1UL : 0UL;
        ulong t1 = b + y1;
        ulong s1 = t1 + c0;
        ulong c1 = (t1 < b ? 1UL : 0UL) | (s1 < c0 ? 1UL : 0UL);
        ulong t2 = c + y2;
        ulong s2 = t2 + c1;
        ulong c2 = (t2 < c ? 1UL : 0UL) | (s2 < c1 ? 1UL : 0UL);

        x0 = s0;
        Unsafe.Add(ref x0, 1) = s1;
        Unsafe.Add(ref x0, 2) = s2;
        return c2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong EstimateQhat(ulong u2, ulong u1, ulong u0, ulong dh, ulong dl, ulong reciprocal)
    {
        ulong qhat;
        ulong rhat;

        if (u2 > dh) return ulong.MaxValue;

        if (u2 == dh)
        {
            qhat = ulong.MaxValue;
            rhat = u1 + dh;
            if (rhat < u1) return qhat;
        }
        else if (X86Base.X64.IsSupported)
        {
            (qhat, rhat) = X86Base.X64.DivRem(u1, u2, dh);
        }
        else
        {
            qhat = UDivRem2By1(u2, reciprocal, dh, u1, out rhat);
        }

        while (true)
        {
            ulong ph = Multiply64(qhat, dl, out ulong pl);
            if (ph < rhat || (ph == rhat && pl <= u0)) break;

            qhat--;
            ulong prev = rhat;
            rhat += dh;
            if (rhat < prev) break;
        }

        return qhat;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Multiply64(ulong a, ulong b, out ulong low)
    {
        if (Bmi2.X64.IsSupported)
        {
            low = a * b;
            return Bmi2.X64.MultiplyNoFlags(a, b);
        }

        if (ArmBase.Arm64.IsSupported)
        {
            low = a * b;
            return ArmBase.Arm64.MultiplyHigh(a, b);
        }

        return Math.BigMul(a, b, out low);
    }

    // Exact floor((2^128 - 1) / d) - 2^64 for normalised d; only the non-x64 path needs it.
    private static ulong Reciprocal2By1(ulong d) => (ulong)(UInt128.MaxValue / d);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong UDivRem2By1(ulong uh, ulong reciprocal, ulong d, ulong ul, out ulong rem)
    {
        ulong hi = Multiply64(reciprocal, uh, out ulong low);
        low += ul;
        ulong q = hi + uh + 1UL;
        if (low < ul) q++;
        ulong r1 = ul - (q * d);
        if (r1 > low) { q--; r1 += d; }
        if (r1 >= d) { q++; r1 -= d; }
        rem = r1;
        return q;
    }
}

[MemoryDiagnoser]
public class Int256SignedDivisionAbBenchmarks
{
    private UInt256[] _a = null!;
    private UInt256[] _b = null!;

    [Params("MostlyPositive", "RandomSigns")]
    public string Signs { get; set; } = "MostlyPositive";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0x5D5D);
        int count = Int256AbInputs.Count;
        _a = new UInt256[count];
        _b = new UInt256[count];
        int negativePercent = Signs == "MostlyPositive" ? 10 : 50;
        for (int i = 0; i < count; i++)
        {
            UInt256 a = Int256AbInputs.RandomWord(random);
            UInt256 b = Int256AbInputs.RandomWord(random);
            if (random.Next(100) < negativePercent) UInt256.Subtract(UInt256.Zero, a, out a);
            if (random.Next(100) < negativePercent) UInt256.Subtract(UInt256.Zero, b, out b);
            if (b.IsOne) b = 3UL;
            _a[i] = a;
            _b[i] = b;
        }

        for (int i = 0; i < count; i++)
        {
            SDivCurrent(in _a[i], in _b[i], out UInt256 q0);
            SDivSignMask(in _a[i], in _b[i], out UInt256 q1);
            SModCurrent(in _a[i], in _b[i], out UInt256 r0);
            SModSignMask(in _a[i], in _b[i], out UInt256 r1);
            if (q0 != q1 || r0 != r1) throw new InvalidOperationException($"Signed mismatch at {i}");
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Int256AbInputs.Count)]
    public ulong SDivBranchOnSigns()
    {
        ulong acc = 0;
        for (int i = 0; i < _a.Length; i++)
        {
            SDivCurrent(in _a[i], in _b[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Int256AbInputs.Count)]
    public ulong SDivSignMaskNegate()
    {
        ulong acc = 0;
        for (int i = 0; i < _a.Length; i++)
        {
            SDivSignMask(in _a[i], in _b[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Int256AbInputs.Count)]
    public ulong SModBranchOnSigns()
    {
        ulong acc = 0;
        for (int i = 0; i < _a.Length; i++)
        {
            SModCurrent(in _a[i], in _b[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Int256AbInputs.Count)]
    public ulong SModSignMaskNegate()
    {
        ulong acc = 0;
        for (int i = 0; i < _a.Length; i++)
        {
            SModSignMask(in _a[i], in _b[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SDivCurrent(in UInt256 a, in UInt256 b, out UInt256 res)
    {
        Unsafe.SkipInit(out res);
        Int256.Int256.Divide(
            in Unsafe.As<UInt256, Int256.Int256>(ref Unsafe.AsRef(in a)),
            in Unsafe.As<UInt256, Int256.Int256>(ref Unsafe.AsRef(in b)),
            out Unsafe.As<UInt256, Int256.Int256>(ref res));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SModCurrent(in UInt256 a, in UInt256 b, out UInt256 res)
    {
        Unsafe.SkipInit(out res);
        Int256.Int256.Mod(
            in Unsafe.As<UInt256, Int256.Int256>(ref Unsafe.AsRef(in a)),
            in Unsafe.As<UInt256, Int256.Int256>(ref Unsafe.AsRef(in b)),
            out Unsafe.As<UInt256, Int256.Int256>(ref res));
    }

    // MIN / -1 needs no special case: the magnitudes divide to 2^255, which is MIN again.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SDivSignMask(in UInt256 a, in UInt256 b, out UInt256 res)
    {
        ulong aSign = (ulong)((long)a.u3 >> 63);
        ulong bSign = (ulong)((long)b.u3 >> 63);
        NegateIf(in a, aSign, out UInt256 aAbs);
        NegateIf(in b, bSign, out UInt256 bAbs);
        UInt256.Divide(in aAbs, in bAbs, out UInt256 quotient);
        NegateIf(in quotient, aSign ^ bSign, out res);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SModSignMask(in UInt256 a, in UInt256 b, out UInt256 res)
    {
        ulong aSign = (ulong)((long)a.u3 >> 63);
        ulong bSign = (ulong)((long)b.u3 >> 63);
        NegateIf(in a, aSign, out UInt256 aAbs);
        NegateIf(in b, bSign, out UInt256 bAbs);
        UInt256.Mod(in aAbs, in bAbs, out UInt256 remainder);
        NegateIf(in remainder, aSign, out res);
    }

    // (x ^ mask) - mask: two's complement negation when mask is all ones, identity when it is zero.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NegateIf(in UInt256 x, ulong mask, out UInt256 res)
    {
        ulong carry = mask & 1;
        ulong r0 = (x.u0 ^ mask) + carry;
        carry = r0 < carry ? 1UL : 0UL;
        ulong r1 = (x.u1 ^ mask) + carry;
        carry = r1 < carry ? 1UL : 0UL;
        ulong r2 = (x.u2 ^ mask) + carry;
        carry = r2 < carry ? 1UL : 0UL;
        ulong r3 = (x.u3 ^ mask) + carry;
        res = new UInt256(r0, r1, r2, r3);
    }
}

[MemoryDiagnoser]
public class Int256AddModAbBenchmarks
{
    private UInt256[] _x = null!;
    private UInt256[] _y = null!;
    private UInt256[] _m = null!;

    [Params("Bn254Reduced", "Bn254BelowTwiceModulus")]
    public string Shape { get; set; } = "Bn254Reduced";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0xADAD);
        int count = Int256AbInputs.Count;
        _x = new UInt256[count];
        _y = new UInt256[count];
        _m = new UInt256[count];
        for (int i = 0; i < count; i++)
        {
            UInt256 m = (i & 1) == 0 ? Int256AbInputs.Bn254R : Int256AbInputs.Bn254Q;
            UInt256 x = Int256AbInputs.RandomBelow(random, m);
            UInt256 y = Int256AbInputs.RandomBelow(random, m);
            if (Shape != "Bn254Reduced")
            {
                if (random.Next(2) == 0) x += m;
                if (random.Next(2) == 0) y += m;
            }

            _x[i] = x;
            _y[i] = y;
            _m[i] = m;
        }

        for (int i = 0; i < count; i++)
        {
            UInt256.AddMod(in _x[i], in _y[i], in _m[i], out UInt256 a);
            AddModTopLimbGate(in _x[i], in _y[i], in _m[i], out UInt256 b);
            if (a != b) throw new InvalidOperationException($"AddMod mismatch at {i}");
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Int256AbInputs.Count)]
    public ulong AddModCurrent()
    {
        ulong acc = 0;
        for (int i = 0; i < _m.Length; i++)
        {
            LibraryAddMod(in _x[i], in _y[i], in _m[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [Benchmark(OperationsPerInvoke = Int256AbInputs.Count)]
    public ulong AddModTopLimbGate()
    {
        ulong acc = 0;
        for (int i = 0; i < _m.Length; i++)
        {
            AddModTopLimbGate(in _x[i], in _y[i], in _m[i], out UInt256 r);
            acc ^= r.u0 ^ r.u3;
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LibraryAddMod(in UInt256 x, in UInt256 y, in UInt256 m, out UInt256 res)
        => UInt256.AddMod(in x, in y, in m, out res);

    // Top limbs no larger than the modulus's nonzero top limb bound both operands below 2m, so one
    // conditional subtraction each and one after the sum replace the general reduction.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddModTopLimbGate(in UInt256 x, in UInt256 y, in UInt256 m, out UInt256 res)
    {
        ulong mTop = m.u3;
        if (mTop != 0 && x.u3 <= mTop && y.u3 <= mTop)
        {
            ReduceOnce(in x, in m, out UInt256 xr);
            ReduceOnce(in y, in m, out UInt256 yr);
            bool carry = UInt256.AddOverflow(in xr, in yr, out UInt256 sum);
            bool borrow = UInt256.SubtractUnderflow(in sum, in m, out UInt256 difference);
            res = carry || !borrow ? difference : sum;
            return;
        }

        UInt256.AddMod(in x, in y, in m, out res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReduceOnce(in UInt256 x, in UInt256 m, out UInt256 res)
    {
        bool borrow = UInt256.SubtractUnderflow(in x, in m, out UInt256 difference);
        res = borrow ? x : difference;
    }
}
