// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public class Bls
    {
        public static Signature Sign(PrivateKey privateKey, Hash256 message)
        {
            Signature signature = new();
            G2.FromHash(message).Multiply(privateKey.KeyBytes).WriteSignature(signature.Bytes);
            return signature;
        }

        public static bool Verify(PublicKey publicKey, Signature signature, Hash256 message)
        {
            bool p1 = Pairing(G1.FromPublicKey(publicKey), G2.FromHash(message));
            bool p2 = Pairing(G1.Generator, G2.FromSignature(signature));
            return p1 && p2;
        }

        public static PublicKey GetPublicKey(PrivateKey privateKey)
        {
            PublicKey publicKey = new()
            {
                Bytes = G1.Generator.Multiply(privateKey.KeyBytes).X
            };
            return publicKey;
        }

        private static bool Pairing(G1 g1, G2 g2)
        {
            Span<byte> encoded = stackalloc byte[384];
            Span<byte> output = stackalloc byte[32];
            g1.Encode(encoded[..128]);
            g2.Encode(encoded[128..]);
            Pairings.BlsPairing(encoded, output);
            return output[31] == 1;
        }

        public class PublicKey
        {
            public byte[] Bytes = new byte[48];
        }

        public class Signature
        {
            public byte[] Bytes = new byte[96];
        }

        public class G1
        {
            public readonly byte[] X = new byte[48];
            public readonly byte[] Y = new byte[48];
            public static readonly G1 Generator = new(
                [0x17,0xF1,0xD3,0xA7,0x31,0x97,0xD7,0x94,0x26,0x95,0x63,0x8C,0x4F,0xA9,0xAC,0x0F,0xC3,0x68,0x8C,0x4F,0x97,0x74,0xB9,0x05,0xA1,0x4E,0x3A,0x3F,0x17,0x1B,0xAC,0x58,0x6C,0x55,0xE8,0x3F,0xF9,0x7A,0x1A,0xEF,0xFB,0x3A,0xF0,0x0A,0xDB,0x22,0xC6,0xBB],
                [0x08,0xB3,0xF4,0x81,0xE3,0xAA,0xA0,0xF1,0xA0,0x9E,0x30,0xED,0x74,0x1D,0x8A,0xE4,0xFC,0xF5,0xE0,0x95,0xD5,0xD0,0x0A,0xF6,0x00,0xDB,0x18,0xCB,0x2C,0x04,0xB3,0xED,0xD0,0x3C,0xC7,0x44,0xA2,0x88,0x8A,0xE4,0x0C,0xAA,0x23,0x29,0x46,0xC5,0xE7,0xE1]
            );
            // private static readonly G1 Generator = new(
            //     [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x01],
            //     [0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x02]
            // );

            public G1(ReadOnlySpan<byte> X, ReadOnlySpan<byte> Y)
            {
                if (X.Length != 48 || Y.Length != 48)
                {
                    throw new Exception("Cannot create G1 point, encoded X and Y must be 48 bytes each.");
                }
                X.CopyTo(this.X);
                Y.CopyTo(this.Y);
            }

            public G1(ReadOnlySpan<byte> X)
            {
                if (X.Length != 48)
                {
                    throw new Exception("Cannot create G1 point, encoded X must be 48 bytes.");
                }

                Span<byte> encoded = stackalloc byte[64];
                Span<byte> output = stackalloc byte[128];
                X.CopyTo(encoded[16..]);
                Pairings.BlsMapToG1(encoded, output);
                output[16..64].CopyTo(this.X);
                output[80..].CopyTo(this.Y);
            }

            public static G1 FromPublicKey(PublicKey k)
            {
                return new G1(k.Bytes);
            }

            public static G1 FromHash(Hash256 x)
            {
                return Generator.Multiply(x.Bytes);
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

            public G1 Multiply(ReadOnlySpan<byte> s)
            {
                if (s.Length != 32)
                {
                    throw new Exception("Scalar must be 32 bytes to multiply with G1 point.");
                }

                Span<byte> encoded = stackalloc byte[160];
                Span<byte> output = stackalloc byte[128];
                Encode(encoded[..128]);
                s.CopyTo(encoded[128..]);
                Pairings.BlsG1Mul(encoded, output);
                return new G1(output[16..64], output[80..]);
            }
        }

        public class G2
        {
            public readonly (byte[], byte[]) X = (new byte[48], new byte[48]);
            public readonly (byte[], byte[]) Y = (new byte[48], new byte[48]);
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

            public G2(ReadOnlySpan<byte> X1, ReadOnlySpan<byte> X2)
            {
                if (X1.Length != 48 || X2.Length != 48)
                {
                    throw new Exception("Cannot create G2 point, encoded X coefficients must be 48 bytes.");
                }

                Span<byte> encoded = stackalloc byte[128];
                Span<byte> output = stackalloc byte[256];
                X1.CopyTo(encoded[16..64]);
                X2.CopyTo(encoded[80..]);
                Pairings.BlsMapToG2(encoded, output);
                output[16..64].CopyTo(X.Item1);
                output[80..128].CopyTo(X.Item2);
                output[144..192].CopyTo(Y.Item1);
                output[208..].CopyTo(Y.Item2);
            }

            public static G2 FromHash(Hash256 x)
            {
                return Generator.Multiply(x.Bytes);
            }

            public static G2 FromSignature(Signature signature)
            {
                return new G2(signature.Bytes.AsSpan()[..48], signature.Bytes.AsSpan()[48..]);
            }

            public void WriteSignature(Span<byte> output)
            {
                X.Item1.CopyTo(output);
                X.Item2.CopyTo(output[48..]);
            }

            public void Encode(Span<byte> output)
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

            public G2 Multiply(ReadOnlySpan<byte> s)
            {
                if (s.Length != 32)
                {
                    throw new Exception("Scalar must be 32 bytes to multiply with G2 point.");
                }

                Span<byte> encoded = stackalloc byte[288];
                Span<byte> output = stackalloc byte[256];
                Encode(encoded[..256]);
                s.CopyTo(encoded[256..]);
                Pairings.BlsG2Mul(encoded, output);
                return new G2(output[16..64], output[80..128], output[144..192], output[208..]);
            }
        }
    }
}
