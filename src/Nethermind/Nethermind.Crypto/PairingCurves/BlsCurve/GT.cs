
// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Crypto.PairingCurves;

public partial class BlsCurve
{
    public class GT(Fq12<BaseField>? x) : IEquatable<GT>
    {
        public readonly Fq12<BaseField>? X = x;

        public static GT operator *(Fq<BaseField> s, GT p)
        {
            if (p.X is null)
            {
                return p;
            }

            return new(Fq12(s) * p.X);
        }
        public override bool Equals(object obj) => Equals(obj as GT);

        public bool Equals(GT p)
        {
            return X == p.X;
        }
        public override int GetHashCode() => X.GetHashCode();

        public static bool operator ==(GT x, GT y)
        {
            return x.Equals(y);
        }

        public static bool operator !=(GT x, GT y) => !x.Equals(y);

        public byte[]? ToBytes()
        {
            if (X is null)
            {
                return null;
            }

            return X.ToBytes();
        }
    }
}
