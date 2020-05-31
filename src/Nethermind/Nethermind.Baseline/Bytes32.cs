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
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Baseline
{
    public class Bytes32 : IEquatable<Bytes32>
    {
        private const int Length = 32;

        private readonly byte[] _bytes;

        public static Bytes32 Wrap(byte[] bytes)
        {
            return new Bytes32(bytes);
        }
        
        private Bytes32(byte[] bytes)
        {
            if (bytes.Length != Length)
            {
                throw new ArgumentException(
                    $"{nameof(Bytes32)} must have exactly {Length} bytes and had {bytes.Length}", nameof(bytes));
            }

            _bytes = bytes;
        }

        public static Bytes32 Zero { get; } = new Bytes32(new byte[Length]);

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(_bytes);
        }

        public bool Equals(Bytes32 other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Bytes.AreEqual(_bytes, other._bytes);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((Bytes32) obj);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(_bytes);
        }
    }
}