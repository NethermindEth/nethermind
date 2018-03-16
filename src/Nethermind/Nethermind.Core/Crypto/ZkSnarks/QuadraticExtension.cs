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

namespace Nethermind.Core.Crypto.ZkSnarks
{
    public abstract class QuadraticExtension<TBase, TExtension> : IField<TExtension>
        where TBase : IField<TBase>
        where TExtension : QuadraticExtension<TBase, TExtension>, new()
    {
        public TBase A { get; protected set; }
        public TBase B { get; protected set; }
        
        /// <summary>
        /// The Schoolbook method computes the product c = ab as
        /// c0 = a0b0 + βa1b1
        /// c1 = a0b1 + a1b0,
        /// which costs 4M + 2A + B
        /// </summary>
        /// <param name="o"></param>
        /// <returns></returns>
        internal TExtension MulSchoolbook(TExtension o)
        {
            TBase a0 = A;
            TBase a1 = B;
            TBase b0 = o.A;
            TBase b1 = o.B;

            TBase c0 = a0.Mul(b0).Add(a1.Mul(b1).MulByNonResidue());
            TBase c1 = a0.Mul(b1).Add(a1.Mul(b0));
            return new TExtension {A = c0, B = c1};
        }

        /// <summary>
        /// https://eprint.iacr.org/2006/471.pdf
        /// The Karatsuba method computes c = ab by first precomputing the values
        /// v0 = a0b0
        /// v1 = a1b1
        /// Then the multiplication is performed as
        /// c0 = v0 + βv1
        /// c1 = (a0 + a1)(b0 + b1) − v0 − v1
        /// which costs 3M + 5A + B
        /// </summary>
        /// <param name="o"></param>
        /// <returns></returns>
        public TExtension MulKaratsuba(TExtension o)
        {
            TBase a0 = A;
            TBase a1 = B;
            TBase b0 = o.A;
            TBase b1 = o.B;

            TBase v0 = a0.Mul(b0);
            TBase v1 = a1.Mul(b1);

            TBase c0 = v0.Add(v1.MulByNonResidue());
            TBase c1 = a0.Add(a1).Mul(b0.Add(b1)).Sub(v0).Sub(v1);

            return new TExtension {A = c0, B = c1};
        }

        public TExtension Mul(TExtension o)
        {
            return MulKaratsuba(o);
        }

        public abstract TExtension MulByNonResidue();

        /// <summary>
        /// c0 = a0^2 + βa1^2
        /// c1 = 2a0a1
        /// M + 2S + 2A + B
        /// </summary>
        /// <returns></returns>
        public TExtension SquaredSchoolbook()
        {
            TBase a0 = A;
            TBase a1 = B;

            TBase c0 = a0.Squared().Add(a1.Squared().MulByNonResidue());
            TBase c1 = a0.Mul(a1).Double();

            return new TExtension {A = c0, B = c1};
        }

        /// <summary>
        /// v0 = a0^2, v1 = a1^2
        /// c0 = v0 + βv1
        /// c1 = (a0 + a1)^2 - v0 - v1
        /// which costs 3S + 4A + B.
        /// </summary>
        /// <returns></returns>
        public TExtension SquaredKaratsuba()
        {
            TBase a0 = A;
            TBase a1 = B;

            TBase v0 = a0.Squared();
            TBase v1 = a1.Squared();

            TBase c0 = v0.Add(v1.MulByNonResidue());
            TBase c1 = a0.Add(a1).Squared().Sub(v0).Sub(v1);

            return new TExtension {A = c0, B = c1};
        }

        /// <summary>
        /// c0 = (a0 + a1)(a0 − a1)
        /// c1 = 2a0a1
        /// which takes 2M + 4A + 2B
        /// </summary>
        /// <returns></returns>
        public TExtension SquaredComplex()
        {
            TBase a0 = A;
            TBase a1 = B;

            TBase v0 = a0.Mul(a1);

            // using Complex squaring
            TBase c0 = a0.Add(a1).Mul(a0.Add(a1.MulByNonResidue())).Sub(v0).Sub(v0.MulByNonResidue());
            TBase c1 = v0.Double();

            return new TExtension {A = c0, B = c1};
        }

        public TExtension Squared()
        {
            return SquaredComplex();
        }

        public TExtension Double()
        {
            return new TExtension {A = A.Add(A), B = B.Add(B)};
        }

        /// <summary>
        /// https://eprint.iacr.org/2010/354.pdf (algorithm 8)
        /// </summary>
        /// <returns></returns>
        public TExtension Inverse()
        {
            TBase a0 = A;
            TBase a1 = B;
            
            TBase t0 = a0.Squared();
            TBase t1 = a1.Squared();
            t0 = t0.Sub(t1.MulByNonResidue());
            t1 = t0.Inverse();

            TBase c0 = a0.Mul(t1);
            TBase c1 = a1.Mul(t1).Negate();

            return new TExtension {A = c0, B = c1};
        }

        public TExtension Negate()
        {
            return new TExtension {A = A.Negate(), B = B.Negate()};
        }

        public bool IsZero()
        {
            return Equals(FieldParams<TExtension>.Zero);
        }

        public bool IsValid()
        {
            return A.IsValid() && B.IsValid();
        }

        public TExtension Add(TExtension o)
        {
            return new TExtension {A = A.Add(o.A), B = B.Add(o.B)};
        }

        public TExtension Sub(TExtension o)
        {
            return new TExtension {A = A.Sub(o.A), B = B.Sub(o.B)};
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (!(obj is QuadraticExtension<TBase, TExtension> other))
            {
                return false;
            }

            return Equals(other);
        }

        public bool Equals(QuadraticExtension<TBase, TExtension> other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return Equals(A, other.A) && Equals(B, other.B);
        }
        
        public bool Equals(TExtension other)
        {
            return Equals((QuadraticExtension<TBase, TExtension>)other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (EqualityComparer<TBase>.Default.GetHashCode(A) * 397) ^ EqualityComparer<TBase>.Default.GetHashCode(B);
            }
        }

        public override string ToString()
        {
            return $"({A}, {B})";
        }
    }
}