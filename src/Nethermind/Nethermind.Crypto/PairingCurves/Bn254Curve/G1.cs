// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Crypto.PairingCurves;

public partial class Bn254Curve
{
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
            return new G1(X, Params.Instance.G1FromX(Fq(X), sign).Item2.ToBytes());
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
            if (output.Length != 64)
            {
                throw new Exception("Encoding G1 point requires 64 bytes.");
            }

            X.CopyTo(output[..32]);
            Y.CopyTo(output[32..]);
        }
    }

}
