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

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core2
{
    public class BlsPublicKey : IEquatable<BlsPublicKey>
    {
        private const int PublicKeyLengthInBytes = 48;
        private Address _address;

        public BlsPublicKey(string hexString)
            : this(Core.Extensions.Bytes.FromHexString(hexString))
        {
        }
        
        public BlsPublicKey(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != PublicKeyLengthInBytes)
            {
                throw new ArgumentException($"{nameof(BlsPublicKey)} should be {PublicKeyLengthInBytes} bytes long", nameof(bytes));
            }

            Bytes = bytes;
        }

        public Address Address => LazyInitializer.EnsureInitialized(ref _address, ComputeAddress);

        public byte[] Bytes { get; }
        
        public bool Equals(BlsPublicKey other)
        {
            return other != null && Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }
        
        private Address ComputeAddress()
        {
            Span<byte> hash = ValueKeccak.Compute(Bytes).BytesAsSpan;
            return new Address(hash.Slice(12).ToArray());
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as BlsPublicKey);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }

        public override string ToString()
        {
            return Bytes.ToHexString(true);
        }

        public string ToString(bool with0X)
        {
            return Bytes.ToHexString(with0X);
        }

        public string ToShortString()
        {
            var value = Bytes.ToHexString(false);
            return $"{value.Substring(0, 6)}...{value.Substring(value.Length - 6)}";;
        }
        
        public static bool operator ==(BlsPublicKey a, BlsPublicKey b)
        {
            if (ReferenceEquals(a, null))
            {
                return ReferenceEquals(b, null);
            }

            if (ReferenceEquals(b, null))
            {
                return false;
            }

            return Core.Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator !=(BlsPublicKey a, BlsPublicKey b)
        {
            return !(a == b);
        }
    }
}