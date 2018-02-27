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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core
{
    // can change it to behave as an array wrapper
    public class Address : IEquatable<Address>
    {
        private const int AddressLengthInBytes = 20;

        public readonly Hex Hex;

        public Address(Keccak keccak)
            : this(keccak.Bytes.Slice(12, 20))
        {
        }

        public static bool IsValidAddress(string hexString, bool allowPrefix)
        {
            if (!(hexString.Length == 40 || allowPrefix && hexString.Length == 42))
            {
                return false;
            }

            bool hasPrefix = hexString.Length == 42;
            if (hasPrefix)
            {
                if (hexString[0] != '0' || hexString[1] != 'x')
                {
                    return false;
                }
            }

            for (int i = hasPrefix ? 2 : 0; i < hexString.Length; i++)
            {
                char c = hexString[i];
                bool isHex = (c >= '0' && c <= '9') ||
                             (c >= 'a' && c <= 'f') ||
                             (c >= 'A' && c <= 'F');

                if (!isHex)
                    return false;
            }

            return true;
        }

        public Address(Hex hex)
        {
            if (hex == null)
            {
                throw new ArgumentNullException(nameof(hex));
            }

            if (hex.ByteLength != AddressLengthInBytes)
            {
                throw new ArgumentException($"{nameof(Address)} should be {AddressLengthInBytes} bytes long", nameof(hex));
            }

            Hex = hex;
        }

        public static Address Zero { get; } = new Address(new byte[20]);

        public bool Equals(Address other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }
            
            if (ReferenceEquals(this, other))
            {
                return true;
            }
            
            return Equals(Hex, other.Hex);
        }

        public string ToString(bool withEip55Checksum)
        {
            // use inside hex?
            return string.Concat("0x", Hex.FromBytes(Hex, false, false, withEip55Checksum));
        }

        /// <summary>
        ///     https://github.com/ethereum/EIPs/issues/55
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return ToString(false);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            return obj.GetType() == GetType() && Equals((Address)obj);
        }

        public override int GetHashCode()
        {
            return Hex.GetHashCode();
        }

        public static bool operator ==(Address a, Address b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            return a?.Equals(b) ?? false;
        }

        public static bool operator !=(Address a, Address b)
        {
            return !(a == b);
        }
    }
}