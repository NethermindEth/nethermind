using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto.ZkSnarks
{
    public abstract class Bn128<T> where T : Field<T>
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

        public abstract Bn128<T> Zero { get; }
        protected abstract Bn128<T> New(T x, T y, T z);
        protected abstract T B { get; }
        protected abstract T One { get; }

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

        public virtual Bn128<T> ToAffine()
        {
            if (IsZero())
            {
                return New(Zero.X, One, Zero.Z); // (0; 1; 0)
            }

            T zInv = Z.Inverse();
            T zInv2 = zInv.Square();
            T zInv3 = zInv2.Mul(zInv);

            T ax = X.Mul(zInv2);
            T ay = Y.Mul(zInv3);

            return New(ax, ay, One);
        }

        /// <summary>
        /// Runs affine transformation and encodes point at infinity as (0; 0; 0)
        /// </summary>
        /// <returns></returns>
        public Bn128<T> ToEthNotation()
        {
            Bn128<T> affine = ToAffine();
            // affine zero is (0; 1; 0), convert to Ethereum zero: (0; 0; 0)
            return affine.IsZero() ? Zero : affine;
        }

        protected bool IsOnCurve()
        {
            if (IsZero())
            {
                return true;
            }

            T z6 = Z.Square().Mul(Z).Square();

            T left = Y.Square(); // y^2
            T right = X.Square().Mul(X).Add(B.Mul(z6)); // x^3 + b * z^6
            return left.Equals(right); // avoid == here as this would call reference equality
        }

        public Bn128<T> Add(Bn128<T> o)
        {
            if (IsZero())
            {
                return o; // 0 + P = P
            }

            if (o.IsZero())
            {
                return this; // P + 0 = P
            }

            T x1 = X, y1 = Y, z1 = Z;
            T x2 = o.X, y2 = o.Y, z2 = o.Z;

            // ported code is started from here
            // next calculations are done in Jacobian coordinates

            T z1Z1 = z1.Square();
            T z2Z2 = z2.Square();

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
            T i = h.Double().Square(); // i = (2 * h)^2
            T j = h.Mul(i); // j = h * i
            T r = s2.Sub(s1).Double(); // r = 2 * (s2 - s1)
            T v = u1.Mul(i); // v = u1 * i
            T zz = z1.Add(z2).Square()
                .Sub(z1.Square()).Sub(z2.Square());

            T x3 = r.Square().Sub(j).Sub(v.Double()); // x3 = r^2 - j - 2 * v
            T y3 = v.Sub(x3).Mul(r).Sub(s1.Mul(j).Double()); // y3 = r * (v - x3) - 2 * (s1 * j)
            T z3 = zz.Mul(h); // z3 = ((z1+z2)^2 - z1^2 - z2^2) * h = zz * h

            return New(x3, y3, z3);
        }

        public Bn128<T> Mul(BigInteger s)
        {
            if (s.IsZero) // P * 0 = 0
            {
                return Zero;
            }

            if (IsZero())
            {
                return this; // 0 * s = 0
            }

            Bn128<T> res = Zero;

            for (int i = s.BitLength() - 1; i >= 0; i--)
            {
                res = res.Double();

                if (s.TestBit(i))
                {
                    res = res.Add(this);
                }
            }

            return res;
        }

        private Bn128<T> Double()
        {
            if (IsZero())
            {
                return this;
            }

            // ported code is started from here
            // next calculations are done in Jacobian coordinates with z = 1

            T a = X.Square(); // a = x^2
            T b = Y.Square(); // b = y^2
            T c = b.Square(); // c = b^2
            T d = X.Add(b).Square().Sub(a).Sub(c);
            d = d.Add(d); // d = 2 * ((x + b)^2 - a - c)
            T e = a.Add(a).Add(a); // e = 3 * a
            T f = e.Square(); // f = e^2

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

            if (!(obj is Bn128<T> other))
            {
                return false;
            }

            return Equals(other);
        }

        public bool Equals(Bn128<T> other)
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

        public static bool operator ==(Bn128<T> a, Bn128<T> b)
        {
            if (ReferenceEquals(a, null) && !ReferenceEquals(b, null))
            {
                return false;
            }
            
            // ReSharper disable once PossibleNullReferenceException
            return a.Equals(b);
        }

        public static bool operator !=(Bn128<T> a, Bn128<T> b)
        {
            return !(a == b);
        }
    }
}