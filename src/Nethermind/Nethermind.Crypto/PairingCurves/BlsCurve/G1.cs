// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Crypto.PairingCurves;

public partial class BlsCurve
{
    public class G1 : IEquatable<G1>
    {
        public readonly byte[] X = new byte[48];
        public readonly byte[] Y = new byte[48];
        public static readonly G1 Zero = new(
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]
        );
        public static readonly G1 Generator = new(
            [0x17, 0xF1, 0xD3, 0xA7, 0x31, 0x97, 0xD7, 0x94, 0x26, 0x95, 0x63, 0x8C, 0x4F, 0xA9, 0xAC, 0x0F, 0xC3, 0x68, 0x8C, 0x4F, 0x97, 0x74, 0xB9, 0x05, 0xA1, 0x4E, 0x3A, 0x3F, 0x17, 0x1B, 0xAC, 0x58, 0x6C, 0x55, 0xE8, 0x3F, 0xF9, 0x7A, 0x1A, 0xEF, 0xFB, 0x3A, 0xF0, 0x0A, 0xDB, 0x22, 0xC6, 0xBB],
            [0x08, 0xB3, 0xF4, 0x81, 0xE3, 0xAA, 0xA0, 0xF1, 0xA0, 0x9E, 0x30, 0xED, 0x74, 0x1D, 0x8A, 0xE4, 0xFC, 0xF5, 0xE0, 0x95, 0xD5, 0xD0, 0x0A, 0xF6, 0x00, 0xDB, 0x18, 0xCB, 0x2C, 0x04, 0xB3, 0xED, 0xD0, 0x3C, 0xC7, 0x44, 0xA2, 0x88, 0x8A, 0xE4, 0x0C, 0xAA, 0x23, 0x29, 0x46, 0xC5, 0xE7, 0xE1]
        );

        public G1(ReadOnlySpan<byte> X, ReadOnlySpan<byte> Y)
        {
            if (X.Length != 48 || Y.Length != 48)
            {
                throw new Exception("Cannot create G1 point, encoded X and Y must be 48 bytes each.");
            }
            X.CopyTo(this.X);
            Y.CopyTo(this.Y);
        }

        public G1((Fq<BaseField>, Fq<BaseField>)? p)
        {
            if (Params.Instance.G1IsOnCurve(p))
            {
                X = p.Value.Item1.ToBytes();
                Y = p.Value.Item2.ToBytes();

                if (!IsInSubgroup())
                {
                    throw new Exception("Invalid G1 point");
                }
            }
            else if (p is not null)
            {
                throw new Exception("Invalid G2 point");
            }
        }

        public (Fq<BaseField>, Fq<BaseField>)? ToFq()
        {
            if (this == Zero)
            {
                return null;
            }
            else
            {
                return (Fq(X), Fq(Y));
            }
        }

        public static G1 FromX(ReadOnlySpan<byte> X, bool sign)
        {
            (Fq<BaseField>, Fq<BaseField>) res = Fq<BaseField>.Sqrt((Fq(X) ^ Fq(3)) + Fq(4));
            Fq<BaseField> Y = sign ? res.Item1 : res.Item2;
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

        public static G1 MapToCurve(byte[] Fq)
        {
            Span<byte> encoded = stackalloc byte[64];
            Span<byte> output = stackalloc byte[128];
            Fq.CopyTo(encoded[16..]);
            Pairings.BlsMapToG1(encoded, output);
            return new G1(output[16..64], output[80..]);
        }

        public static G1 operator -(G1 p)
        {
            if (p == Zero)
            {
                return Zero;
            }

            (Fq<BaseField>, Fq<BaseField>)? tmp = p.ToFq();
            return new((tmp.Value.Item1, -tmp.Value.Item2));
        }

        public bool IsInSubgroup()
        {
            return (SubgroupOrder.ToBigEndianByteArray(32) * this) == Zero;
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
}
