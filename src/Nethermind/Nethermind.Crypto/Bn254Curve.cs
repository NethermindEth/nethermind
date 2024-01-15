// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Crypto;

public class Bn254Curve
{
    public static readonly BigInteger BaseFieldOrder = new([0x30, 0x64, 0x4E, 0x72, 0xE1, 0x31, 0xA0, 0x29, 0xB8, 0x50, 0x45, 0xB6, 0x81, 0x81, 0x58, 0x5D, 0x97, 0x81, 0x6A, 0x91, 0x68, 0x71, 0xCA, 0x8D, 0x3C, 0x20, 0x8C, 0x16, 0xD8, 0x7C, 0xFD, 0x47], true, true);
    public static readonly BigInteger FpQuadraticNonResidue = BaseFieldOrder - 1;
    public static readonly byte[] SubgroupOrder = [0x30, 0x64, 0x4E, 0x72, 0xE1, 0x31, 0xA0, 0x29, 0xB8, 0x50, 0x45, 0xB6, 0x81, 0x81, 0x58, 0x5D, 0x28, 0x33, 0xE8, 0x48, 0x79, 0xB9, 0x70, 0x91, 0x43, 0xE1, 0xF5, 0x93, 0xF0, 0x00, 0x00, 0x01];
    public static readonly byte[] SubgroupOrderMinusOne = [0x30, 0x64, 0x4E, 0x72, 0xE1, 0x31, 0xA0, 0x29, 0xB8, 0x50, 0x45, 0xB6, 0x81, 0x81, 0x58, 0x5D, 0x28, 0x33, 0xE8, 0x48, 0x79, 0xB9, 0x70, 0x91, 0x43, 0xE1, 0xF5, 0x93, 0xF0, 0x00, 0x00, 0x00];

    public static bool Pairing(G1 g1, G2 g2)
    {
        Span<byte> encoded = stackalloc byte[192];
        Span<byte> output = stackalloc byte[32];
        g1.Encode(encoded[..64]);
        g2.Encode(encoded[64..]);
        Pairings.Bn254Pairing(encoded, output);
        return output[31] == 1;
    }

    public static bool Pairing2(G1 a1, G2 a2, G1 b1, G2 b2)
    {
        Span<byte> encoded = stackalloc byte[192 * 2];
        Span<byte> output = stackalloc byte[32];
        a1.Encode(encoded[..64]);
        a2.Encode(encoded[64..]);
        b1.Encode(encoded[192..]);
        b2.Encode(encoded[256..]);
        Pairings.Bn254Pairing(encoded, output);
        return output[31] == 1;
    }

    public static bool PairingsEqual(G1 a1, G2 a2, G1 b1, G2 b2)
    {
        return Pairing2(-a1, a2, b1, b2);
    }

    public class G1 : IEquatable<G1>
    {
        public readonly byte[] X = new byte[32];
        public readonly byte[] Y = new byte[32];
        public static readonly G1 Zero = new(
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]
        );
        public static readonly G1 Generator = new(
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01],
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02]
        );

        public G1(ReadOnlySpan<byte> X, ReadOnlySpan<byte> Y)
        {
            if (X.Length != 32 || Y.Length != 32)
            {
                throw new Exception("Cannot create G1 point, encoded X and Y must be 32 bytes each.");
            }
            X.CopyTo(this.X);
            Y.CopyTo(this.Y);
        }

        public static G1 FromX(ReadOnlySpan<byte> X, bool sign)
        {
            (Fp, Fp) res = Fp.Sqrt((new Fp(X) ^ 3) + 3);
            Fp Y = sign ? res.Item1 : res.Item2;
            return new G1(X, Y.ToBytes());
        }

        public static G1 FromScalar(UInt256 x)
        {
            return x.ToBigEndian() * Generator;
        }
        public override bool Equals(object obj) => Equals(obj as G1);

        public bool Equals(G1 p)
        {
            return X.SequenceEqual(p.X) && Y.SequenceEqual(p.Y);
        }
        public override int GetHashCode() => (X, Y).GetHashCode();

        public static bool operator ==(G1 p, G1 q)
        {
            return p.Equals(q);
        }

        public static bool operator !=(G1 p, G1 q) => !p.Equals(q);

        public static G1 operator *(ReadOnlySpan<byte> s, G1 p)
        {
            if (s.Length != 32)
            {
                throw new Exception("Scalar must be 32 bytes to multiply with G1 point.");
            }

            Span<byte> encoded = stackalloc byte[64 + 32];
            Span<byte> output = stackalloc byte[64];
            p.Encode(encoded[..64]);
            s.CopyTo(encoded[64..]);
            Pairings.Bn254Mul(encoded, output);
            return new G1(output[..32], output[32..]);
        }

        public static G1 operator *(UInt256 s, G1 p)
        {
            return s.ToBigEndian() * p;
        }

        public static G1 operator +(G1 p, G1 q)
        {
            Span<byte> encoded = stackalloc byte[128];
            Span<byte> output = stackalloc byte[64];
            p.Encode(encoded[..64]);
            q.Encode(encoded[64..]);
            Pairings.Bn254Add(encoded, output);
            return new G1(output[..32], output[32..]);
        }

        public static G1 operator -(G1 p)
        {
            return SubgroupOrderMinusOne * p;
        }

        internal void Encode(Span<byte> output)
        {
            if (output.Length != 64)
            {
                throw new Exception("Encoding G1 point requires 64 bytes.");
            }

            X.CopyTo(output[..32]);
            Y.CopyTo(output[32..]);
        }
    }

    public class G2 : IEquatable<G2>
    {
        public readonly (byte[], byte[]) X = (new byte[32], new byte[32]);
        public readonly (byte[], byte[]) Y = (new byte[32], new byte[32]);
        public static readonly G2 Zero = new(
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]
        );
        public static readonly G2 Generator = new(
            [0x18, 0x00, 0xDE, 0xEF, 0x12, 0x1F, 0x1E, 0x76, 0x42, 0x6A, 0x00, 0x66, 0x5E, 0x5C, 0x44, 0x79, 0x67, 0x43, 0x22, 0xD4, 0xF7, 0x5E, 0xDA, 0xDD, 0x46, 0xDE, 0xBD, 0x5C, 0xD9, 0x92, 0xF6, 0xED],
            [0x19, 0x8E, 0x93, 0x93, 0x92, 0x0D, 0x48, 0x3A, 0x72, 0x60, 0xBF, 0xB7, 0x31, 0xFB, 0x5D, 0x25, 0xF1, 0xAA, 0x49, 0x33, 0x35, 0xA9, 0xE7, 0x12, 0x97, 0xE4, 0x85, 0xB7, 0xAE, 0xF3, 0x12, 0xC2],
            [0x12, 0xC8, 0x5E, 0xA5, 0xDB, 0x8C, 0x6D, 0xEB, 0x4A, 0xAB, 0x71, 0x80, 0x8D, 0xCB, 0x40, 0x8F, 0xE3, 0xD1, 0xE7, 0x69, 0x0C, 0x43, 0xD3, 0x7B, 0x4C, 0xE6, 0xCC, 0x01, 0x66, 0xFA, 0x7D, 0xAA],
            [0x09, 0x06, 0x89, 0xD0, 0x58, 0x5F, 0xF0, 0x75, 0xEC, 0x9E, 0x99, 0xAD, 0x69, 0x0C, 0x33, 0x95, 0xBC, 0x4B, 0x31, 0x33, 0x70, 0xB3, 0x8E, 0xF3, 0x55, 0xAC, 0xDA, 0xDC, 0xD1, 0x22, 0x97, 0x5B]
        );

        public G2(ReadOnlySpan<byte> X0, ReadOnlySpan<byte> X1, ReadOnlySpan<byte> Y0, ReadOnlySpan<byte> Y1)
        {
            if (X0.Length != 32 || X1.Length != 32 || Y0.Length != 32 || Y1.Length != 32)
            {
                throw new Exception("Cannot create G2 point, encoded values must be 32 bytes each.");
            }
            X0.CopyTo(X.Item1);
            X1.CopyTo(X.Item2);
            Y0.CopyTo(Y.Item1);
            Y1.CopyTo(Y.Item2);
        }

        public static G2 FromScalar(UInt256 x)
        {
            return x.ToBigEndian() * Generator;
        }

        public static G2 FromX(ReadOnlySpan<byte> X0, ReadOnlySpan<byte> X1, bool sign)
        {
            if (X0.Length != 32 || X1.Length != 32)
            {
                throw new Exception("Cannot create G2 point from X, encoded values must be 32 bytes each.");
            }

            // y ^ 2 = sqrt(x ^ 3 + 4(1 + i)) = a + bi

            Fp c0 = X0;
            Fp c1 = X1;

            Fp a = (c0 ^ 3) - (3 * c0 * (c1 ^ 2)) + 4;
            Fp b = -(c1 ^ 3) + 3 * (c0 ^ 2) * c1 + 4;
            (Fp, Fp) l = Fp.Sqrt((a ^ 2) + (b ^ 2));

            // test all possible signs
            for (int i = 0; i < 2; i++)
            {
                Fp lCurrent = i == 0 ? l.Item1 : l.Item2;
                for (int j = 0; j < 2; j++)
                {
                    (Fp, Fp) y0 = Fp.Sqrt(((-a) + lCurrent) / 2);
                    (Fp, Fp) y1 = Fp.Sqrt((a + lCurrent) / 2);

                    Fp y0Current = j == 0 ? y0.Item1 : y0.Item2;

                    for (int k = 0; k < 2; k++)
                    {
                        Fp y1Current = k == 0 ? y1.Item1 : y1.Item2;

                        if ((y0Current < y1Current) != sign)
                        {
                            continue;
                        }

                        if (2 * y0Current * y1Current != b)
                        {
                            continue;
                        }

                        if ((y0Current ^ 2) - (y1Current ^ 2) != a)
                        {
                            continue;
                        }

                        return new G2(X0, X1, y0Current.ToBytes(), y1Current.ToBytes());
                    }
                }
            }

            throw new Exception("Could not twist invalid x coordinate.");
        }

        public override bool Equals(object obj) => Equals(obj as G1);

        public bool Equals(G2 p)
        {
            return X.Item1.SequenceEqual(p.X.Item1) && X.Item2.SequenceEqual(p.X.Item2) && Y.Item1.SequenceEqual(p.Y.Item1) && Y.Item2.SequenceEqual(p.Y.Item2);
        }
        public override int GetHashCode() => (X, Y).GetHashCode();

        public static bool operator ==(G2 p, G2 q)
        {
            return p.Equals(q);
        }

        public static bool operator !=(G2 p, G2 q) => !p.Equals(q);

        public static G2 operator *(ReadOnlySpan<byte> s, G2 p)
        {
            if (s.Length != 32)
            {
                throw new Exception("Scalar must be 32 bytes to multiply with G2 point.");
            }

            // Span<byte> encoded = stackalloc byte[64 + 32];
            // Span<byte> output = stackalloc byte[64];
            // p.X.Item1.CopyTo(encoded[..32]);
            // p.Y.Item1.CopyTo(encoded[32..]);
            // s.CopyTo(encoded[64..]);
            // Pairings.Bn254Mul(encoded, output);

            // Span<byte> encodedI = stackalloc byte[64 + 32];
            // Span<byte> outputI = stackalloc byte[64];
            // p.X.Item2.CopyTo(encodedI[..32]);
            // p.Y.Item2.CopyTo(encodedI[32..]);
            // s.CopyTo(encodedI[64..]);
            // Pairings.Bn254Mul(encodedI, outputI);

            // return new G2(output[..32], outputI[..32], output[32..], outputI[32..]);
            throw new NotImplementedException();
        }

        public static G2 operator *(UInt256 s, G2 p)
        {
            return s.ToBigEndian() * p;
        }

        public static G2 operator +(G2 p, G2 q)
        {
            // Span<byte> encoded = stackalloc byte[128];
            // Span<byte> output = stackalloc byte[64];
            // p.X.Item1.CopyTo(encoded[..32]);
            // p.Y.Item1.CopyTo(encoded[32..]);
            // q.X.Item1.CopyTo(encoded[64..]);
            // q.Y.Item1.CopyTo(encoded[96..]);
            // Pairings.Bn254Add(encoded, output);

            // Span<byte> encodedI = stackalloc byte[128];
            // Span<byte> outputI = stackalloc byte[64];
            // p.X.Item2.CopyTo(encodedI[..32]);
            // p.Y.Item2.CopyTo(encodedI[32..]);
            // q.X.Item2.CopyTo(encodedI[64..]);
            // q.Y.Item2.CopyTo(encodedI[96..]);
            // Pairings.Bn254Add(encodedI, outputI);

            // return new G2(output[..32], outputI[..32], output[32..], outputI[32..]);
            throw new NotImplementedException();
        }

        public static G2 operator -(G2 p)
        {
            return SubgroupOrderMinusOne * p;
        }

        internal void Encode(Span<byte> output)
        {
            if (output.Length != 128)
            {
                throw new Exception("Encoding G2 point requires 128 bytes.");
            }

            X.Item1.CopyTo(output[..32]);
            X.Item2.CopyTo(output[32..]);
            Y.Item1.CopyTo(output[64..]);
            Y.Item2.CopyTo(output[96..]);
        }
    }

    public class Fp
    {
        internal BigInteger _value;

        public Fp(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != 32)
            {
                throw new Exception("Field point must have 32 bytes");
            }
            _value = Normalise(new BigInteger(bytes, true, true));
        }

        public Fp(BigInteger value)
        {
            _value = Normalise(value);
        }

        private static BigInteger Normalise(BigInteger x)
        {
            BigInteger unnormalised = x % BaseFieldOrder;
            return (unnormalised.Sign == 1) ? unnormalised : (BaseFieldOrder + unnormalised);
        }

        public static implicit operator Fp(byte[] v) => new(v);
        public static implicit operator Fp(ReadOnlySpan<byte> v) => new(v);
        public static implicit operator Fp(BigInteger v) => new(v);
        public static implicit operator Fp(int v) => new(v);

        public byte[] ToBytes()
        {
            return _value.ToBigEndianByteArray(32);
        }

        public static (Fp, Fp) Sqrt(Fp x)
        {
            Fp res = x ^ ((BaseFieldOrder + 1) / 4);
            return (res, -res);
        }

        public static Fp operator -(Fp x)
        {
            return BaseFieldOrder - x._value;
        }

        public static Fp operator ^(Fp x, Fp exp)
        {
            return BigInteger.ModPow(x._value, exp._value, BaseFieldOrder);
        }

        public static Fp operator +(Fp x, Fp y)
        {
            return x._value + y._value;
        }

        public static bool operator <(Fp x, Fp y)
        {
            return x._value < y._value;
        }

        public static bool operator >(Fp x, Fp y)
        {
            return x._value > y._value;
        }
        public override bool Equals(object obj) => Equals(obj as Fp);

        public bool Equals(Fp p)
        {
            return _value == p._value;
        }
        public override int GetHashCode() => _value.GetHashCode();

        public static bool operator ==(Fp x, Fp y)
        {
            return x._value == y._value;
        }

        public static bool operator !=(Fp x, Fp y) => !x.Equals(y);

        // todo: move to helper
        private static BigInteger gcdExtended(BigInteger a, BigInteger b, ref BigInteger x, ref BigInteger y)
        {
            if (a == 0)
            {
                x = 0;
                y = 1;
                return b;
            }

            BigInteger x1 = 1, y1 = 1;
            BigInteger gcd = gcdExtended(b % a, a, ref x1, ref y1);

            x = y1 - (b / a) * x1;
            y = x1;

            return gcd;
        }

        public static Fp Inv(Fp c)
        {
            BigInteger x = 1;
            BigInteger y = 1;
            gcdExtended(c._value, BaseFieldOrder, ref x, ref y);
            return x;
        }

        public static Fp operator *(Fp x, Fp y)
        {
            return x._value * y._value;
        }

        public static Fp operator -(Fp x, Fp y)
        {
            return x + (-y);
        }

        public static Fp operator /(Fp x, Fp y)
        {
            return x * Inv(y);
        }
    }
}
