// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

// MULMOD by field moduli and by unknown moduli, calling the referenced Int256 package as shipped, so base and head
// differ only by the package build. Operands in 64-byte aligned native memory as in Int256LibraryAbBenchmarks.
[MemoryDiagnoser]
public unsafe class Int256ReductionAbBenchmarks
{
    private const int Count = 1024;

    private static readonly UInt256 Bn254R = Hex("30644e72e131a029b85045b68181585d2833e84879b9709143e1f593f0000001");
    private static readonly UInt256 Bn254P = Hex("30644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd47");
    private static readonly UInt256 Prime251 = Hex("0800000000000011000000000000000000000000000000000000000000000001");
    private static readonly UInt256 Secp256k1P = Hex("fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f");
    private static readonly UInt256 Secp256k1N = Hex("fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141");
    private static readonly UInt256 P256P = Hex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
    private static readonly UInt256 P256N = Hex("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
    private static readonly UInt256 Bls12381R = Hex("73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001");

    private UInt256* _x;
    private UInt256* _y;
    private UInt256* _m;

    [Params("Bn254R", "Bn254P", "Prime251", "Prime251Unreduced", "Bn254RUnreduced", "Secp256k1P", "Secp256k1N",
        "P256P", "P256N", "Bls12381R", "RandomFullWidth", "RandomShifted")]
    public string Shape { get; set; } = "Bn254R";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0x2ED0);
        _x = Int256LibraryAbBenchmarks.Allocate();
        _y = Int256LibraryAbBenchmarks.Allocate();
        _m = Int256LibraryAbBenchmarks.Allocate();
        for (int i = 0; i < Count; i++)
        {
            UInt256 x = Random256(random);
            UInt256 y = Random256(random);
            UInt256 m;
            bool reduce = true;
            switch (Shape)
            {
                case "Bn254R": m = Bn254R; break;
                case "Bn254P": m = Bn254P; break;
                case "Prime251": m = Prime251; break;
                case "Prime251Unreduced": m = Prime251; reduce = false; break;
                case "Bn254RUnreduced": m = Bn254R; reduce = false; break;
                case "Secp256k1P": m = Secp256k1P; break;
                case "Secp256k1N": m = Secp256k1N; break;
                case "P256P": m = P256P; break;
                case "P256N": m = P256N; break;
                case "Bls12381R": m = Bls12381R; break;
                case "RandomFullWidth": m = Random256(random) | (UInt256.One << 255); reduce = false; break;
                default: m = (Random256(random) >> random.Next(1, 64)) | (UInt256.One << 192); reduce = false; break;
            }

            if (reduce)
            {
                UInt256.Mod(in x, in m, out x);
                UInt256.Mod(in y, in m, out y);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MultiplyMod(in UInt256 x, in UInt256 y, in UInt256 m, out UInt256 res) => UInt256.MultiplyMod(in x, in y, in m, out res);

    private static UInt256 Hex(string value)
    {
        byte[] bytes = Convert.FromHexString(value);
        return new UInt256(bytes, isBigEndian: true);
    }

    private static UInt256 Random256(Random random)
    {
        Span<byte> bytes = stackalloc byte[32];
        random.NextBytes(bytes);
        return new UInt256(bytes, isBigEndian: false);
    }
}
