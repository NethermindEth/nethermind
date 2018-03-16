/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */


using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto.ZkSnarks
{
    public abstract class Bn128<T, TSelf> where T : IField<T> where TSelf : Bn128<T, TSelf>
    {
        public T X { get; }
        public T Y { get; }
        public T Z { get; }

        protected Bn128(T x, T y, T z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public abstract TSelf Zero { get; }
        public abstract TSelf New(T x, T y, T z);
        public abstract T B { get; }
        public abstract T One { get; }

        public bool IsZero()
        {
            return Z.IsZero();
        }

        protected bool IsValid()
        {
            // check whether coordinates belongs to the Field
            if (!X.IsValid() || !Y.IsValid() || !Z.IsValid())
            {
                return false;
            }

            // check whether point is on the curve
            if (!IsOnCurve())
            {
                return false;
            }

            return true;
        }

        public virtual TSelf ToAffine()
        {
            if (IsZero())
            {
                return New(Zero.X, One, Zero.Z); // (0; 1; 0)
            }

            T zInv = Z.Inverse();
            T zInv2 = zInv.Squared();
            T zInv3 = zInv2.Mul(zInv);

            T ax = X.Mul(zInv2);
            T ay = Y.Mul(zInv3);

            return New(ax, ay, One);
        }

        /// <summary>
        /// Runs affine transformation and encodes point at infinity as (0; 0; 0)
        /// </summary>
        /// <returns></returns>
        public TSelf ToEthNotation()
        {
            TSelf affine = ToAffine();
            // affine zero is (0; 1; 0), convert to Ethereum zero: (0; 0; 0)
            return affine.IsZero() ? Zero : affine;
        }

        protected bool IsOnCurve()
        {
            if (IsZero())
            {
                return true;
            }

            // The M-type sextic twist curve is defined by equation y'^2 = x'^3 + b * s when elliptic curve E(F_p) is set to be y^2 = x^3 + b
            // and represent of F_{p^12} is set to be F_{p^2}[u]/(u^6 - s), where s is in F_{p^2}^*.
            // The corresponding map I: E'(F_{p^2}) -> E(F_{p^12}) is (x', y') -> (x' * s^{-1} * z^4, y' * s^{-1} * z^3), with z^6 = s.
            T z6 = Z.Squared().Mul(Z).Squared();

            T left = Y.Squared(); // y^2
            T right = X.Squared().Mul(X).Add(B.Mul(z6)); // x^3 + b * z^6
            return left.Equals(right); // avoid == here as this would call reference equality
        }

        public TSelf Add(TSelf o)
        {
            if (IsZero())
            {
                return o; // 0 + P = P
            }

            if (o.IsZero())
            {
                return (TSelf)this; // P + 0 = P
            }

            T x1 = X, y1 = Y, z1 = Z;
            T x2 = o.X, y2 = o.Y, z2 = o.Z;

            // ported code is started from here
            // next calculations are done in Jacobian coordinates

            T z1Z1 = z1.Squared();
            T z2Z2 = z2.Squared();

            T u1 = x1.Mul(z2Z2);
            T u2 = x2.Mul(z1Z1);

            T z1Cubed = z1.Mul(z1Z1);
            T z2Cubed = z2.Mul(z2Z2);

            T s1 = y1.Mul(z2Cubed); // s1 = y1 * Z2^3
            T s2 = y2.Mul(z1Cubed); // s2 = y2 * Z1^3

            if (u1.Equals(u2) && s1.Equals(s2))
            {
                return Double(); // P + P = 2P
            }

            T h = u2.Sub(u1); // h = u2 - u1
            T i = h.Double().Squared(); // i = (2 * h)^2
            T j = h.Mul(i); // j = h * i
            T r = s2.Sub(s1).Double(); // r = 2 * (s2 - s1)
            T v = u1.Mul(i); // v = u1 * i
            T zz = z1.Add(z2).Squared()
                .Sub(z1.Squared()).Sub(z2.Squared());

            T x3 = r.Squared().Sub(j).Sub(v.Double()); // x3 = r^2 - j - 2 * v
            T y3 = v.Sub(x3).Mul(r).Sub(s1.Mul(j).Double()); // y3 = r * (v - x3) - 2 * (s1 * j)
            T z3 = zz.Mul(h); // z3 = ((z1+z2)^2 - z1^2 - z2^2) * h = zz * h

            return New(x3, y3, z3);
        }

        public TSelf Mul(BigInteger s)
        {
            if (s.IsZero) // P * 0 = 0
            {
                return Zero;
            }

            if (IsZero())
            {
                return (TSelf)this; // 0 * s = 0
            }

            TSelf res = Zero;

            for (int i = s.BitLength() - 1; i >= 0; i--)
            {
                res = res.Double();

                if (s.TestBit(i))
                {
                    res = res.Add((TSelf)this);
                }
            }

            return res;
        }

        private TSelf Double()
        {
            if (IsZero())
            {
                return (TSelf)this;
            }

            // ported code is started from here
            // next calculations are done in Jacobian coordinates with z = 1

            T a = X.Squared(); // a = x^2
            T b = Y.Squared(); // b = y^2
            T c = b.Squared(); // c = b^2
            T d = X.Add(b).Squared().Sub(a).Sub(c);
            d = d.Add(d); // d = 2 * ((x + b)^2 - a - c)
            T e = a.Add(a).Add(a); // e = 3 * a
            T f = e.Squared(); // f = e^2

            T x3 = f.Sub(d.Add(d)); // rx = f - 2 * d
            T y3 = e.Mul(d.Sub(x3)).Sub(c.Double().Double().Double()); // ry = e * (d - rx) - 8 * c
            T z3 = Y.Mul(Z).Double(); // z3 = 2 * y * z

            return New(x3, y3, z3);
        }
        
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (!(obj is TSelf other))
            {
                return false;
            }

            return Equals(other);
        }

        public bool Equals(TSelf other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }
            
            return Equals(X, other.X) && Equals(Y, other.Y) && Equals(Z, other.Z);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = EqualityComparer<T>.Default.GetHashCode(X);
                hashCode = (hashCode * 397) ^ EqualityComparer<T>.Default.GetHashCode(Y);
                hashCode = (hashCode * 397) ^ EqualityComparer<T>.Default.GetHashCode(Z);
                return hashCode;
            }
        }

        public static bool operator ==(Bn128<T, TSelf> a, Bn128<T, TSelf> b)
        {
            return a?.Equals(b) ?? ReferenceEquals(b, null);
        }

        public static bool operator !=(Bn128<T, TSelf> a, Bn128<T, TSelf> b)
        {
            return !(a == b);
        }
    }
}