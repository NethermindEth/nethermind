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

using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto.ZkSnarks
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class Fp : Field<Fp>
    {
        private readonly BigInteger _value;

        public Fp(BigInteger value)
        {
            _value = value;
        }
        
        public Fp(byte[] bytes)
        {
            _value = bytes.ToUnsignedBigInteger();
        }

        public static readonly Fp InverseOf2 = new Fp(new BigInteger(2).ModInverse(Parameters.P));
        
        public static readonly Fp Zero = new Fp(BigInteger.Zero);
        public static readonly Fp One = new Fp(BigInteger.One);
        public static readonly Fp NonResidue = new Fp(BigInteger.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208582"));

        public override Fp Add(Fp o)
        {
            return _value + o._value % Parameters.P;
        }

        public Fp2 Mul(Fp2 fp2)
        {
            return new Fp2(fp2.A.Mul(this), fp2.B.Mul(this));
        }

        public override Fp Mul(Fp o)
        {
            return _value * o._value % Parameters.P;
        }

        public override Fp Sub(Fp o)
        {
            return _value - o._value % Parameters.P;
        }

        public override Fp Square()
        {
            return _value * _value % Parameters.P;
        }

        public override Fp Double()
        {
            return _value + _value % Parameters.P;
        }

        public override Fp Inverse()
        {
            return _value.ModInverse(Parameters.P);
        }

        public override Fp Negate()
        {
            return -_value % Parameters.P;
        }

        public override bool IsZero()
        {
            return _value.IsZero;
        }

        public override bool IsValid()
        {
            return _value <= Parameters.P;
        }

        public static implicit operator Fp(int value)
        {
            return new Fp(value);
        }

        public static implicit operator Fp(uint value)
        {
            return new Fp(value);
        }

        public static implicit operator Fp(long value)
        {
            return new Fp(value);
        }

        public static implicit operator Fp(ulong value)
        {
            return new Fp(value);
        }

        public static implicit operator Fp(BigInteger value)
        {
            return new Fp(value);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (!(obj is Fp other))
            {
                return false;
            }

            return Equals(other);
        }

        public override bool Equals(Fp other)
        {
            // ReSharper disable once ImpureMethodCallOnReadonlyValueField
            return _value.Equals(other._value);
        }

        public override int GetHashCode()
        {
            // ReSharper disable once ImpureMethodCallOnReadonlyValueField
            return _value.GetHashCode();
        }

        public static bool operator ==(Fp a, Fp b)
        {
            if (ReferenceEquals(a, null) && !ReferenceEquals(b, null))
            {
                return false;
            }
            
            // ReSharper disable once PossibleNullReferenceException
            return a.Equals(b);
        }

        public static bool operator !=(Fp a, Fp b)
        {
            return !(a == b);
        }

        public byte[] GetBytes()
        {
            return _value.ToBigEndianByteArray();
        }
    }
}