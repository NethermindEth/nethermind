// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class SubgroupChecks
{
    internal static readonly BigInteger BlsBaseFieldOrder = new([0x1a, 0x01, 0x11, 0xea, 0x39, 0x7f, 0xe6, 0x9a, 0x4b, 0x1b, 0xa7, 0xb6, 0x43, 0x4b, 0xac, 0xd7, 0x64, 0x77, 0x4b, 0x84, 0xf3, 0x85, 0x12, 0xbf, 0x67, 0x30, 0xd2, 0xa0, 0xf6, 0xb0, 0xf6, 0x24, 0x1e, 0xab, 0xff, 0xfe, 0xb1, 0x53, 0xff, 0xff, 0xb9, 0xfe, 0xff, 0xff, 0xff, 0xff, 0xaa, 0xab], true, true);
    internal static readonly BigInteger BlsSubgroupOrder = new([0x73, 0xed, 0xa7, 0x53, 0x29, 0x9d, 0x7d, 0x48, 0x33, 0x39, 0xd8, 0x08, 0x09, 0xa1, 0xd8, 0x05, 0x53, 0xbd, 0xa4, 0x02, 0xff, 0xfe, 0x5b, 0xfe, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x01], true, true);
    internal static readonly BigInteger Beta = new([0x5F, 0x19, 0x67, 0x2F, 0xDF, 0x76, 0xCE, 0x51, 0xBA, 0x69, 0xC6, 0x07, 0x6A, 0x0F, 0x77, 0xEA, 0xDD, 0xB3, 0xA9, 0x3B, 0xE6, 0xF8, 0x96, 0x88, 0xDE, 0x17, 0xD8, 0x13, 0x62, 0x0A, 0x00, 0x02, 0x2E, 0x01, 0xFF, 0xFF, 0xFF, 0xFE, 0xFF, 0xFE], true, true);
    internal static readonly BigInteger X = Normalise(new BigInteger(15132376222941642752));
    internal static readonly byte[] G1SubgroupMultiplier = (X * X % BlsSubgroupOrder).ToBigEndianByteArray(32);
    internal static readonly byte[] G2SubgroupMultiplier = X.ToBigEndianByteArray(32);
    internal static readonly Fp2 R = new(0, new BigInteger([0x1A, 0x01, 0x11, 0xEA, 0x39, 0x7F, 0xE6, 0x99, 0xEC, 0x02, 0x40, 0x86, 0x63, 0xD4, 0xDE, 0x85, 0xAA, 0x0D, 0x85, 0x7D, 0x89, 0x75, 0x9A, 0xD4, 0x89, 0x7D, 0x29, 0x65, 0x0F, 0xB8, 0x5F, 0x9B, 0x40, 0x94, 0x27, 0xEB, 0x4F, 0x49, 0xFF, 0xFD, 0x8B, 0xFD, 0x00, 0x00, 0x00, 0x00, 0xAA, 0xAD], true, true));
    internal static readonly Fp2 S = new(new BigInteger([0x13, 0x52, 0x03, 0xE6, 0x01, 0x80, 0xA6, 0x8E, 0xE2, 0xE9, 0xC4, 0x48, 0xD7, 0x7A, 0x2C, 0xD9, 0x1C, 0x3D, 0xED, 0xD9, 0x30, 0xB1, 0xCF, 0x60, 0xEF, 0x39, 0x64, 0x89, 0xF6, 0x1E, 0xB4, 0x5E, 0x30, 0x44, 0x66, 0xCF, 0x3E, 0x67, 0xFA, 0x0A, 0xF1, 0xEE, 0x7B, 0x04, 0x12, 0x1B, 0xDE, 0xA2], true, true), new BigInteger([0x06, 0xAF, 0x0E, 0x04, 0x37, 0xFF, 0x40, 0x0B, 0x68, 0x31, 0xE3, 0x6D, 0x6B, 0xD1, 0x7F, 0xFE, 0x48, 0x39, 0x5D, 0xAB, 0xC2, 0xD3, 0x43, 0x5E, 0x77, 0xF7, 0x6E, 0x17, 0x00, 0x92, 0x41, 0xC5, 0xEE, 0x67, 0x99, 0x2F, 0x72, 0xEC, 0x05, 0xF4, 0xC8, 0x10, 0x84, 0xFB, 0xED, 0xE3, 0xCC, 0x09], true, true));

    [SkipLocalsInit]
    public static bool G1IsInSubGroup(ReadOnlySpan<byte> p)
    {
        Span<byte> buf = stackalloc byte[3 * 128];
        Span<byte> mulArgs = buf[128..(2 * 128 + 32)];
        Span<byte> addArgs = buf[..(2 * 128)];
        Span<byte> res = buf[(2 * 128)..];

        p.CopyTo(mulArgs);
        G1SubgroupMultiplier.CopyTo(mulArgs[128..]);

        if (!Pairings.BlsG1Mul(mulArgs, addArgs[..128]))
        {
            return false;
        }

        Phi(p, addArgs[128..]);

        if (!Pairings.BlsG1Add(addArgs, res))
        {
            return false;
        }

        return res.IndexOfAnyExcept((byte)0) == -1;
    }

    [SkipLocalsInit]
    public static bool G2IsInSubGroup(ReadOnlySpan<byte> p)
    {
        Span<byte> buf = stackalloc byte[3 * 256];
        Span<byte> mulArgs = buf[256..(2 * 256 + 32)];
        Span<byte> addArgs = buf[..(2 * 256)];
        Span<byte> res = buf[(2 * 256)..];

        p.CopyTo(mulArgs);
        G2SubgroupMultiplier.CopyTo(mulArgs[256..]);
        if (!Pairings.BlsG2Mul(mulArgs, addArgs[..256]))
        {
            return false;
        }

        Psi(p, addArgs[256..]);

        if (!Pairings.BlsG2Add(addArgs, res))
        {
            return false;
        }

        return res.IndexOfAnyExcept((byte)0) == -1;
    }

    internal static void Phi(ReadOnlySpan<byte> p, Span<byte> res)
    {
        BigInteger x = new(p[..64], true, true);
        x = Beta * x % BlsBaseFieldOrder;
        x.ToBigEndianByteArray(64).CopyTo(res);
        p[64..].CopyTo(res[64..]);
    }

    internal static void Psi(ReadOnlySpan<byte> p, Span<byte> res)
    {
        Fp2 x = new Fp2(p[..128]).Conjugate() * R;
        Fp2 y = new Fp2(p[128..]).Conjugate() * S;
        x.ToBytes(res[..128]);
        y.ToBytes(res[128..]);
    }

    internal static BigInteger Normalise(in BigInteger x)
    {
        BigInteger unnormalised = x % BlsBaseFieldOrder;
        return unnormalised >= 0 ? unnormalised : (BlsBaseFieldOrder + unnormalised);
    }

    internal class Fp2
    {
        public BigInteger c0, c1;

        public Fp2(BigInteger c0, BigInteger c1)
        {
            this.c0 = c0;
            this.c1 = c1;
        }

        public Fp2(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != 128)
            {
                throw new Exception("Could not decode Fp2 point, incorrect length.");
            }
            c0 = new(bytes[..64], true, true);
            c1 = new(bytes[64..], true, true);
        }

        public static Fp2 operator *(Fp2 a, Fp2 b)
            => new Fp2(Normalise((a.c0 * b.c0) - (a.c1 * b.c1)), Normalise((a.c1 * b.c0) + (a.c0 * b.c1)));

        public Fp2 Conjugate()
        {
            c1 = Normalise(-c1);
            return this;
        }

        public void ToBytes(Span<byte> res)
        {
            c0.ToBigEndianByteArray(64).CopyTo(res);
            c1.ToBigEndianByteArray(64).CopyTo(res[64..]);
        }
    }
}
