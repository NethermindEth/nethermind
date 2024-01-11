// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto
{
    public class Bls
    {
        internal static readonly BigInteger BaseFieldOrder = new([0x1a,0x01,0x11,0xea,0x39,0x7f,0xe6,0x9a,0x4b,0x1b,0xa7,0xb6,0x43,0x4b,0xac,0xd7,0x64,0x77,0x4b,0x84,0xf3,0x85,0x12,0xbf,0x67,0x30,0xd2,0xa0,0xf6,0xb0,0xf6,0x24,0x1e,0xab,0xff,0xfe,0xb1,0x53,0xff,0xff,0xb9,0xfe,0xff,0xff,0xff,0xff,0xaa,0xab], true, true);
        internal static readonly BigInteger FpQuadraticNonResidue = BaseFieldOrder - 1;
        internal static readonly byte[] SubgroupOrder = [0x73,0xed,0xa7,0x53,0x29,0x9d,0x7d,0x48,0x33,0x39,0xd8,0x08,0x09,0xa1,0xd8,0x05,0x53,0xbd,0xa4,0x02,0xff,0xfe,0x5b,0xfe,0xff,0xff,0xff,0xff,0x00,0x00,0x00,0x01];
        internal static readonly byte[] SubgroupOrderMinusOne = [0x73,0xed,0xa7,0x53,0x29,0x9d,0x7d,0x48,0x33,0x39,0xd8,0x08,0x09,0xa1,0xd8,0x05,0x53,0xbd,0xa4,0x02,0xff,0xfe,0x5b,0xfe,0xff,0xff,0xff,0xff,0x00,0x00,0x00,0x00];
        internal static readonly byte[] Cryptosuite = ASCIIEncoding.ASCII.GetBytes("BLS_SIG_BLS12381G1_XMD:SHA-256_SSWU_RO_NUL_");

        public static Signature Sign(PrivateKey privateKey, ReadOnlySpan<byte> message)
        {
            return (privateKey.KeyBytes.Reverse().ToArray() * G1.HashToCurve(message, Cryptosuite)).ToSignature();
        }

        public static bool Verify(PublicKey publicKey, Signature signature, ReadOnlySpan<byte> message)
        {
            return PairingsEqual(G1.FromSignature(signature), G2.Generator, G1.HashToCurve(message, Cryptosuite), G2.FromPublicKey(publicKey));
        }

        public static PublicKey GetPublicKey(PrivateKey privateKey)
        {
            return (privateKey.KeyBytes.Reverse().ToArray() * G2.Generator).ToPublicKey();
        }

        public static bool Pairing(G1 g1, G2 g2)
        {
            Span<byte> encoded = stackalloc byte[384];
            Span<byte> output = stackalloc byte[32];
            g1.Encode(encoded[..128]);
            g2.Encode(encoded[128..]);
            Pairings.BlsPairing(encoded, output);
            return output[31] == 1;
        }

        public static bool Pairing2(G1 a1, G2 a2, G1 b1, G2 b2)
        {
            Span<byte> encoded = stackalloc byte[384*2];
            Span<byte> output = stackalloc byte[32];
            a1.Encode(encoded[..128]);
            a2.Encode(encoded[128..384]);
            b1.Encode(encoded[384..512]);
            b2.Encode(encoded[512..]);
            Pairings.BlsPairing(encoded, output);
            return output[31] == 1;
        }

        public static bool PairingsEqual(G1 a1, G2 a2, G1 b1, G2 b2)
        {
            return Pairing2(-a1, a2, b1, b2);
        }

        internal static void CompressPoint(ReadOnlySpan<byte> p, bool sign, Span<byte> res)
        {
            p.CopyTo(res);

            if (sign)
            {
                res[0] |= 0x20;
            }

            // indicates that byte is compressed
            res[0] |= 0x80;
        }

        internal static bool DecompressPoint(ReadOnlySpan<byte> p, Span<byte> res)
        {
            bool sign = (p[0] & 0x20) == 0x20;
            p.CopyTo(res);
            res[0] &= 0x1F; // mask out top 3 bits
            return sign;
        }

        public struct PublicKey
        {
            public byte[] Bytes = new byte[96];

            public PublicKey()
            {
            }
        }

        public struct Signature
        {
            public byte[] Bytes = new byte[48];

            public Signature()
            {
            }
        }

        public class Fp : IEquatable<Fp>
        {
            internal BigInteger _value;

            public Fp(ReadOnlySpan<byte> bytes)
            {
                if (bytes.Length != 48)
                {
                    throw new Exception("Field point must have 48 bytes");
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
                return _value.ToBigEndianByteArray(48);
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

            public override bool Equals(object obj) => Equals(obj as G1);

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

            public static Fp Twist(Fp x, bool sign)
            {
                (Fp, Fp) res = Sqrt((x ^ 3) + 4);
                return sign ? res.Item1 : res.Item2;
            }

            public static (Fp, Fp) TwistG2(Fp c0, Fp c1, bool sign)
            {
                // y ^ 2 = sqrt(x ^ 3 + 4(1 + i)) = a + bi

                Fp a = (c0 ^ 3) - (3 * c0 * (c1 ^ 2)) + 4;
                Fp b = -(c1 ^ 3) + 3 * (c0 ^ 2) * c1 + 4;
                (Fp, Fp) l = Sqrt((a ^ 2) + (b ^ 2));

                // test all possible signs
                for (int i = 0; i < 2; i++)
                {
                    Fp lCurrent = i == 0 ? l.Item1 : l.Item2;
                    for (int j = 0; j < 2; j++)
                    {
                        (Fp, Fp) y0 = Sqrt(((-a) + lCurrent) / 2);
                        (Fp, Fp) y1 = Sqrt((a + lCurrent) / 2);

                        Fp y0Current = j == 0 ? y0.Item1 : y0.Item2;

                        for (int k = 0; k < 2; k++)
                        {
                            Fp y1Current = k == 0 ? y1.Item1 : y1.Item2;

                            if ((y0Current < y1Current) != sign)
                            {
                                continue;
                            }

                            if ((y0Current ^ 2)  - (y1Current ^ 2) != a)
                            {
                                continue;
                            }

                            if (2 * y0Current * y1Current != b)
                            {
                                continue;
                            }

                            return (y0Current, y1Current);
                        }
                    }
                }
                
                throw new Exception("Could not twist invalid x coordinate.");
            }
        }

        public class G1 : IEquatable<G1>
        {
            public readonly byte[] X = new byte[48];
            public readonly byte[] Y = new byte[48];
            public static readonly G1 Zero = new(
                [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00],
                [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00]
            );
            public static readonly G1 Generator = new(
                [0x17,0xF1,0xD3,0xA7,0x31,0x97,0xD7,0x94,0x26,0x95,0x63,0x8C,0x4F,0xA9,0xAC,0x0F,0xC3,0x68,0x8C,0x4F,0x97,0x74,0xB9,0x05,0xA1,0x4E,0x3A,0x3F,0x17,0x1B,0xAC,0x58,0x6C,0x55,0xE8,0x3F,0xF9,0x7A,0x1A,0xEF,0xFB,0x3A,0xF0,0x0A,0xDB,0x22,0xC6,0xBB],
                [0x08,0xB3,0xF4,0x81,0xE3,0xAA,0xA0,0xF1,0xA0,0x9E,0x30,0xED,0x74,0x1D,0x8A,0xE4,0xFC,0xF5,0xE0,0x95,0xD5,0xD0,0x0A,0xF6,0x00,0xDB,0x18,0xCB,0x2C,0x04,0xB3,0xED,0xD0,0x3C,0xC7,0x44,0xA2,0x88,0x8A,0xE4,0x0C,0xAA,0x23,0x29,0x46,0xC5,0xE7,0xE1]
            );
            private static readonly UInt256 HEff = 0xd201000000010001;
            private static int InputLength = 64;

            public G1(ReadOnlySpan<byte> X, ReadOnlySpan<byte> Y)
            {
                if (X.Length != 48 || Y.Length != 48)
                {
                    throw new Exception("Cannot create G1 point, encoded X and Y must be 48 bytes each.");
                }
                X.CopyTo(this.X);
                Y.CopyTo(this.Y);
            }

            public static G1 FromScalar(UInt256 x)
            {
                return x.ToBigEndian() * Generator;
            }

            // parameters:
            // https://datatracker.ietf.org/doc/html/rfc9380#section-8.8.1
            // implementation:
            // https://datatracker.ietf.org/doc/html/rfc9380#section-3
            public static G1 HashToCurve(ReadOnlySpan<byte> msg, ReadOnlySpan<byte> dst)
            {
                List<byte[]> u = HashToField(msg, 2, dst);
                // n.b. MapToCurve implements clear_cofactor internally
                G1 q0 = MapToCurve(u[0]);
                G1 q1 = MapToCurve(u[1]);
                return q0 + q1;
            }

            internal static G1 ClearCofactor(G1 p)
            {
                return HEff * p;
            }

            // https://datatracker.ietf.org/doc/html/rfc9380#section-5.2
            internal static List<byte[]> HashToField(ReadOnlySpan<byte> msg, int count, ReadOnlySpan<byte> dst)
            {
                List<byte[]> u = [];

                int lenInBytes = count * InputLength;
                Span<byte> uniformBytes = stackalloc byte[lenInBytes];
                ExpandMessageXmd(msg, dst, lenInBytes, uniformBytes);
                for (int i = 0; i < count; i++)
                {
                    int elmOffset = InputLength * i;
                    ReadOnlySpan<byte> tv = uniformBytes[elmOffset..(elmOffset+InputLength)];
                    BigInteger e = new BigInteger(tv, true, true) % BaseFieldOrder;
                    u.Add(e.ToBigEndianByteArray(48));
                }

                return u;
            }

            // https://datatracker.ietf.org/doc/html/rfc9380#section-5.3.1
            internal static void ExpandMessageXmd(ReadOnlySpan<byte> msg, ReadOnlySpan<byte> dst, int lenInBytes, Span<byte> res)
            {
                int ell = (lenInBytes / 32) + (lenInBytes % 32 == 0 ? 0 : 1);
                if (ell > 255)
                {
                    throw new Exception();
                }

                // DST_prime = DST || I2OSP(len(DST), 1)
                Span<byte> dstPrime = stackalloc byte[dst.Length + 1];
                dst.CopyTo(dstPrime);
                dstPrime[dst.Length] = (byte)dst.Length;

                // msg_prime = Z_pad || msg || I2OSP(len_in_bytes, 2) || I2OSP(0, 1) || DST_prime
                Span<byte> msgPrime = stackalloc byte[64 + msg.Length + 2 + 1 + dstPrime.Length];
                msg.CopyTo(msgPrime[64..]);
                BinaryPrimitives.WriteUInt16BigEndian(msgPrime[(64+msg.Length)..], (ushort)lenInBytes);
                dstPrime.CopyTo(msgPrime[(67 + msg.Length)..]);

                Span<byte> hashInput = stackalloc byte[32 + 1 + dstPrime.Length];

                Span<byte> b0 = SHA256.HashData(msgPrime);

                b0.CopyTo(hashInput);
                dstPrime.CopyTo(hashInput[33..]);

                for (byte i = 1; i <= ell; i++)
                {
                    hashInput[32] = i;
                    Span<byte> b = SHA256.HashData(hashInput);
                    b.CopyTo(res[((i-1)*32)..]);

                    // set first 32 bytes of next hash input
                    b.Xor(b0);
                    b.CopyTo(hashInput);
                }
            }

            public static G1 FromSignature(Signature signature)
            {
                G1 P = Zero;
                bool sign = DecompressPoint(signature.Bytes, P.X);
                Fp.Twist(P.X, sign).ToBytes().CopyTo(P.Y.AsSpan());
                return P;
            }

            public Signature ToSignature()
            {
                Signature s = new();
                Fp y = Fp.Twist(X, true);
                bool sign = Enumerable.SequenceEqual(Y, y.ToBytes());
                CompressPoint(X, sign, s.Bytes);
                return s;
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

                Span<byte> encoded = stackalloc byte[160];
                Span<byte> output = stackalloc byte[128];
                p.Encode(encoded[..128]);
                s.CopyTo(encoded[128..]);
                Pairings.BlsG1Mul(encoded, output);
                return new G1(output[16..64], output[80..]);
            }

            public static G1 operator *(UInt256 s, G1 p)
            {
                return s.ToBigEndian() * p;
            }

            public static G1 operator +(G1 p, G1 q)
            {
                Span<byte> encoded = stackalloc byte[256];
                Span<byte> output = stackalloc byte[128];
                p.Encode(encoded[..128]);
                q.Encode(encoded[128..]);
                Pairings.BlsG1Add(encoded, output);
                return new G1(output[16..64], output[80..]);
            }

            internal static G1 MapToCurve(byte[] Fp)
            {
                Span<byte> encoded = stackalloc byte[64];
                Span<byte> output = stackalloc byte[128];
                Fp.CopyTo(encoded[16..]);
                Pairings.BlsMapToG1(encoded, output);
                return new G1(output[16..64], output[80..]);
            }

            public static G1 operator -(G1 p)
            {
                return SubgroupOrderMinusOne * p;
            }

            internal void Encode(Span<byte> output)
            {
                if (output.Length != 128)
                {
                    throw new Exception("Encoding G1 point requires 128 bytes.");
                }

                X.CopyTo(output[16..]);
                Y.CopyTo(output[80..]);
            }
        }

        public class G2 : IEquatable<G2>
        {
            public readonly (byte[], byte[]) X = (new byte[48], new byte[48]);
            public readonly (byte[], byte[]) Y = (new byte[48], new byte[48]);
            public static readonly G2 Zero = new(
                [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00],
                [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00],
                [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00],
                [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00]
            );
            public static readonly G2 Generator = new(
                [0x02,0x4a,0xa2,0xb2,0xf0,0x8f,0x0a,0x91,0x26,0x08,0x05,0x27,0x2d,0xc5,0x10,0x51,0xc6,0xe4,0x7a,0xd4,0xfa,0x40,0x3b,0x02,0xb4,0x51,0x0b,0x64,0x7a,0xe3,0xd1,0x77,0x0b,0xac,0x03,0x26,0xa8,0x05,0xbb,0xef,0xd4,0x80,0x56,0xc8,0xc1,0x21,0xbd,0xb8],
                [0x13,0xe0,0x2b,0x60,0x52,0x71,0x9f,0x60,0x7d,0xac,0xd3,0xa0,0x88,0x27,0x4f,0x65,0x59,0x6b,0xd0,0xd0,0x99,0x20,0xb6,0x1a,0xb5,0xda,0x61,0xbb,0xdc,0x7f,0x50,0x49,0x33,0x4c,0xf1,0x12,0x13,0x94,0x5d,0x57,0xe5,0xac,0x7d,0x05,0x5d,0x04,0x2b,0x7e],
                [0x0c,0xe5,0xd5,0x27,0x72,0x7d,0x6e,0x11,0x8c,0xc9,0xcd,0xc6,0xda,0x2e,0x35,0x1a,0xad,0xfd,0x9b,0xaa,0x8c,0xbd,0xd3,0xa7,0x6d,0x42,0x9a,0x69,0x51,0x60,0xd1,0x2c,0x92,0x3a,0xc9,0xcc,0x3b,0xac,0xa2,0x89,0xe1,0x93,0x54,0x86,0x08,0xb8,0x28,0x01],
                [0x06,0x06,0xc4,0xa0,0x2e,0xa7,0x34,0xcc,0x32,0xac,0xd2,0xb0,0x2b,0xc2,0x8b,0x99,0xcb,0x3e,0x28,0x7e,0x85,0xa7,0x63,0xaf,0x26,0x74,0x92,0xab,0x57,0x2e,0x99,0xab,0x3f,0x37,0x0d,0x27,0x5c,0xec,0x1d,0xa1,0xaa,0xa9,0x07,0x5f,0xf0,0x5f,0x79,0xbe]
            );

            public G2(ReadOnlySpan<byte> X1, ReadOnlySpan<byte> X2, ReadOnlySpan<byte> Y1, ReadOnlySpan<byte> Y2)
            {
                if (X1.Length != 48 || X2.Length != 48 || Y1.Length != 48  || Y2.Length != 48)
                {
                    throw new Exception("Cannot create G2 point, encoded coefficients must be 48 bytes each.");
                }
                X1.CopyTo(X.Item1);
                X2.CopyTo(X.Item2);
                Y1.CopyTo(Y.Item1);
                Y2.CopyTo(Y.Item2);
            }

            public static G2 FromScalar(UInt256 x)
            {
                return x.ToBigEndian() * Generator;
            }

            public static G2 FromPublicKey(PublicKey pk)
            {

                G2 P = Zero;
                bool sign = DecompressPoint(pk.Bytes.AsSpan()[..48], P.X.Item2);
                pk.Bytes.AsSpan()[48..].CopyTo(P.X.Item1);

                (Fp, Fp) y = Fp.TwistG2(P.X.Item1, P.X.Item2, sign);
                y.Item1.ToBytes().CopyTo(P.Y.Item1.AsSpan());
                y.Item2.ToBytes().CopyTo(P.Y.Item2.AsSpan());

                return P;
            }

            public PublicKey ToPublicKey()
            {
                PublicKey pk = new();
                bool sign = new Fp(Y.Item1) < new Fp(Y.Item2);
                CompressPoint(X.Item2, sign, pk.Bytes.AsSpan()[..48]);
                X.Item1.CopyTo(pk.Bytes.AsSpan()[48..]);
                return pk;
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

                Span<byte> encoded = stackalloc byte[288];
                Span<byte> output = stackalloc byte[256];
                p.Encode(encoded[..256]);
                s.CopyTo(encoded[256..]);
                Pairings.BlsG2Mul(encoded, output);
                return new G2(output[16..64], output[80..128], output[144..192], output[208..]);
            }
            
            public static G2 operator *(UInt256 s, G2 p)
            {
                return s.ToBigEndian() * p;
            }

            public static G2 operator +(G2 p, G2 q)
            {
                Span<byte> encoded = stackalloc byte[512];
                Span<byte> output = stackalloc byte[256];
                p.Encode(encoded[..256]);
                q.Encode(encoded[256..]);
                Pairings.BlsG2Add(encoded, output);
                return new G2(output[16..64], output[80..128], output[144..192], output[208..]);
            }

            public static G2 operator -(G2 p)
            {
                return SubgroupOrderMinusOne * p;
            }

            internal void Encode(Span<byte> output)
            {
                if (output.Length != 256)
                {
                    throw new Exception("Encoding G2 point requires 256 bytes.");
                }

                X.Item1.CopyTo(output[16..]);
                X.Item2.CopyTo(output[80..]);
                Y.Item1.CopyTo(output[144..]);
                Y.Item2.CopyTo(output[208..]);
            }
        }
    }
}
