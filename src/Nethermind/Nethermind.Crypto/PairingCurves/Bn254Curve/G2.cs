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
            [0x19, 0x8E, 0x93, 0x93, 0x92, 0x0D, 0x48, 0x3A, 0x72, 0x60, 0xBF, 0xB7, 0x31, 0xFB, 0x5D, 0x25, 0xF1, 0xAA, 0x49, 0x33, 0x35, 0xA9, 0xE7, 0x12, 0x97, 0xE4, 0x85, 0xB7, 0xAE, 0xF3, 0x12, 0xC2],
            [0x18, 0x00, 0xDE, 0xEF, 0x12, 0x1F, 0x1E, 0x76, 0x42, 0x6A, 0x00, 0x66, 0x5E, 0x5C, 0x44, 0x79, 0x67, 0x43, 0x22, 0xD4, 0xF7, 0x5E, 0xDA, 0xDD, 0x46, 0xDE, 0xBD, 0x5C, 0xD9, 0x92, 0xF6, 0xED],
            [0x09, 0x06, 0x89, 0xD0, 0x58, 0x5F, 0xF0, 0x75, 0xEC, 0x9E, 0x99, 0xAD, 0x69, 0x0C, 0x33, 0x95, 0xBC, 0x4B, 0x31, 0x33, 0x70, 0xB3, 0x8E, 0xF3, 0x55, 0xAC, 0xDA, 0xDC, 0xD1, 0x22, 0x97, 0x5B],
            [0x12, 0xC8, 0x5E, 0xA5, 0xDB, 0x8C, 0x6D, 0xEB, 0x4A, 0xAB, 0x71, 0x80, 0x8D, 0xCB, 0x40, 0x8F, 0xE3, 0xD1, 0xE7, 0x69, 0x0C, 0x43, 0xD3, 0x7B, 0x4C, 0xE6, 0xCC, 0x01, 0x66, 0xFA, 0x7D, 0xAA]
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

        public G2((Fq2<BaseField>, Fq2<BaseField>)? p)
        {
            if (Params.Instance.G2IsOnCurve(p))
            {
                X.Item1 = p.Value.Item1.a.ToBytes();
                X.Item2 = p.Value.Item1.b.ToBytes();
                Y.Item1 = p.Value.Item2.a.ToBytes();
                Y.Item2 = p.Value.Item2.b.ToBytes();

                if (!IsInSubgroup())
                {
                    throw new Exception("Invalid G2 point");
                }
            }
            else if (p is not null)
            {
                throw new Exception("Invalid G2 point");
            }
        }

        public static G2 FromScalar(UInt256 x)
        {
            return x.ToBigEndian() * Generator;
        }

        public (Fq2<BaseField>, Fq2<BaseField>)? ToFq()
        {
            if (this == Zero)
            {
                return null;
            }
            else
            {
                return (Fq2(X.Item1, X.Item2), Fq2(Y.Item1, Y.Item2));
            }
        }

        public static G2 FromX(ReadOnlySpan<byte> X0, ReadOnlySpan<byte> X1, bool sign)
        {
            if (X0.Length != 32 || X1.Length != 32)
            {
                throw new Exception("Cannot create G2 point from X, encoded values must be 32 bytes each.");
            }

            Fq2<BaseField> y = Params.Instance.G2FromX(Fq2(X0, X1), sign).Item2;
            return new G2(X0, X1, y.a.ToBytes(), y.b.ToBytes());
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

            return new UInt256(s, true) * p;
        }

        public static G2 operator *(UInt256 s, G2 p)
        {
            var tmp = p.ToFq();
            (Fq2<BaseField>, Fq2<BaseField>)? r = FieldArithmetic<BaseField>.G2Multiply(s, tmp, Params.Instance);
            return new(r);
        }

        public static G2 operator +(G2 p, G2 q)
        {
            (Fq2<BaseField>, Fq2<BaseField>)? r = FieldArithmetic<BaseField>.G2Add(p.ToFq(), q.ToFq(), BaseField.Instance);
            return new(r);
        }

        public static G2 operator -(G2 p)
        {
            if (p == Zero)
            {
                return Zero;
            }

            (Fq2<BaseField>, Fq2<BaseField>)? tmp = p.ToFq();
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
                throw new Exception("Encoding G2 point requires 128 bytes.");
            }

            X.Item2.CopyTo(output[..32]);
            X.Item1.CopyTo(output[32..64]);
            Y.Item2.CopyTo(output[64..96]);
            Y.Item1.CopyTo(output[96..]);
        }
    }
}
