//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;

namespace Nethermind.Core2.Types
{
    public struct Shard : IEquatable<Shard>
    {
        private readonly ulong _value;

        public Shard(ulong value)
        {
            _value = value;
        }

        public static Shard Zero { get; } = new Shard(0);

        public override bool Equals(object? obj)
        {
            return obj is Shard shard && Equals(shard);
        }

        public bool Equals(Shard other)
        {
            return _value == other._value;
        }

        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }

        public static Shard Min(Shard val1, Shard val2)
        {
            return new Shard(Math.Min(val1._value, val2._value));
        }

        public static Shard operator +(Shard left, Shard right)
        {
            return new Shard(left._value + right._value);
        }

        public static Shard operator /(Shard left, ulong right)
        {
            return new Shard(left._value / right);
        }

        public static bool operator ==(Shard left, Shard right)
        {
            return left.Equals(right);
        }

        public static explicit operator ulong(Shard shard) => shard._value;

        public static bool operator !=(Shard left, Shard right)
        {
            return !(left == right);
        }

        public static Shard operator %(Shard left, Shard right)
        {
            return new Shard(left._value % right._value);
        }

        public static Shard operator *(Shard left, ulong right)
        {
            return new Shard(left._value * right);
        }

        public static Shard operator -(Shard left, Shard right)
        {
            return new Shard(left._value - right._value);
        }

        public override string ToString()
        {
            return _value.ToString();
        }
    }
}