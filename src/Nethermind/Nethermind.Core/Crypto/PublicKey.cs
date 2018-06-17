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
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto
{
    public class PublicKey : IEquatable<PublicKey>
    {
        private const int PublicKeyWithPrefixLengthInBytes = 65;
        private const int PublicKeyLengthInBytes = 64;
        private Address _address;

        private byte[] _prefixedBytes;

        // TODO: consider Hex here
        public PublicKey(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != PublicKeyLengthInBytes && bytes.Length != PublicKeyWithPrefixLengthInBytes)
            {
                throw new ArgumentException($"{nameof(PublicKey)} should be {PublicKeyLengthInBytes} bytes long", nameof(bytes));
            }

            if (bytes.Length == PublicKeyWithPrefixLengthInBytes && bytes[0] != 0x04)
            {
                throw new ArgumentException($"Expected prefix of 0x04 for {PublicKeyWithPrefixLengthInBytes} bytes long {nameof(PublicKey)}");
            }

            Bytes = bytes.Slice(bytes.Length - 64, 64);
        }

        public Address Address => LazyInitializer.EnsureInitialized(ref _address, ComputeAddress);

        public byte[] Bytes { get; }

        public byte[] PrefixedBytes
        {
            get { return LazyInitializer.EnsureInitialized(ref _prefixedBytes, () => Extensions.Bytes.Concat(0x04, Bytes)); }
        }

        public bool Equals(PublicKey other)
        {
            if (other == null)
            {
                return false;
            }

            return Extensions.Bytes.UnsafeCompare(Bytes, other.Bytes);
        }

        private Address ComputeAddress()
        {
            byte[] hash = Keccak.Compute(Bytes).Bytes;
            byte[] last160Bits = new byte[20];
            Buffer.BlockCopy(hash, 12, last160Bits, 0, 20);
            return new Address(last160Bits);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as PublicKey);
        }

        public override int GetHashCode()
        {
            return Bytes.GetXxHashCode();
        }

        public override string ToString()
        {
            return Hex.FromBytes(Bytes, true);
        }
        
        public string ToString(bool with0X)
        {
            return Hex.FromBytes(Bytes, with0X);
        }

        public string ToShortString()
        {
            var value = Hex.FromBytes(Bytes, false);
            return value.Substring(value.Length - 12);
        }
    }
}