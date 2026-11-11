// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;

namespace Nethermind.Precompiles.Benchmark;

/// <summary>MODEXP on the input shapes contracts send: field inversions as base^(p - 2) mod p and general exponentiations.</summary>
[MemoryDiagnoser]
public class ModExpShapesBenchmark
{
    private const string Bn254Base = "30644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd47";
    private const string Bn254Scalar = "30644e72e131a029b85045b68181585d2833e84879b9709143e1f593f0000001";
    private const string Secp256k1Base = "fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f";
    private const string Secp256k1Order = "fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141";
    private const string Secp256r1Base = "ffffffff00000001000000000000000000000000ffffffffffffffffffffffff";
    private const string Secp256r1Order = "ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551";
    private const string SampleBase = "1c0e6a3f9b2d4e8a7c5b3d1f0e2a4c6b8d9f1e3a5c7b9d0f2e4a6c8b1d3f5e7a";

    public readonly struct Case(string name, byte[] input)
    {
        public byte[] Input { get; } = input;
        public override string ToString() => name;
    }

    public static IEnumerable<Case> Cases()
    {
        yield return Inverse("inverse_bn254_fq", Bn254Base);
        yield return Inverse("inverse_bn254_fr", Bn254Scalar);
        yield return Inverse("inverse_secp256k1_p", Secp256k1Base);
        yield return Inverse("inverse_secp256k1_n", Secp256k1Order);
        yield return Inverse("inverse_secp256r1_p", Secp256r1Base);
        yield return Inverse("inverse_secp256r1_n", Secp256r1Order);

        // 32-byte shapes that do not invert: a Fermat test, a square root in the BN254 base field and a non-prime modulus
        BigInteger bn254 = Parse(Bn254Base);
        yield return new Case("fermat_secp256k1_p", Build(Pad(3), ToBytes(Parse(Secp256k1Base) - 1), Convert.FromHexString(Secp256k1Base)));
        yield return new Case("sqrt_bn254_fq", Build(Convert.FromHexString(SampleBase), ToBytes((bn254 + 1) / 4), Convert.FromHexString(Bn254Base)));
        yield return new Case("pow_256_composite", Build(Convert.FromHexString(SampleBase), Convert.FromHexString(Secp256r1Order), ToBytes(Parse(Bn254Base) * 3)));

        // RSA-style verification: exponent 65537 with a 2048-bit odd modulus
        byte[] modulus = new byte[256];
        Random random = new(42);
        random.NextBytes(modulus);
        modulus[0] |= 0x80;
        modulus[^1] |= 1;
        byte[] signature = new byte[256];
        random.NextBytes(signature);
        signature[0] &= 0x7f;
        yield return new Case("rsa2048_e65537", Build(signature, [0x01, 0x00, 0x01], modulus));
    }

    [ParamsSource(nameof(Cases))]
    public Case Input { get; set; }

    [Benchmark]
    public bool Run()
    {
        Result<byte[]> result = ModExpPrecompile.Instance.Run(Input.Input, Osaka.Instance);
        return result.IsSuccess;
    }

    private static Case Inverse(string name, string primeHex) =>
        new(name, Build(Convert.FromHexString(SampleBase), ToBytes(Parse(primeHex) - 2), Convert.FromHexString(primeHex)));

    private static byte[] Build(byte[] baseBytes, byte[] exponent, byte[] modulus)
    {
        byte[] input = new byte[96 + baseBytes.Length + exponent.Length + modulus.Length];
        Pad(baseBytes.Length).CopyTo(input, 0);
        Pad(exponent.Length).CopyTo(input, 32);
        Pad(modulus.Length).CopyTo(input, 64);
        baseBytes.CopyTo(input, 96);
        exponent.CopyTo(input, 96 + baseBytes.Length);
        modulus.CopyTo(input, 96 + baseBytes.Length + exponent.Length);
        return input;
    }

    private static byte[] Pad(int value) => ToBytes(value);

    private static BigInteger Parse(string hex) => new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);

    private static byte[] ToBytes(BigInteger value)
    {
        byte[] result = new byte[32];
        byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        raw.CopyTo(result, result.Length - raw.Length);
        return result;
    }
}
