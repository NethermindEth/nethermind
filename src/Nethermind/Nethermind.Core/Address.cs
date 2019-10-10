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
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core
{
    public class Address : IEquatable<Address>
    {
        public const int ByteLength = 20;
        private const int HexCharsCount = 2 * ByteLength; // 5a4eab120fb44eb6684e5e32785702ff45ea344d
        private const int PrefixedHexCharsCount = 2 + HexCharsCount; // 0x5a4eab120fb44eb6684e5e32785702ff45ea344d
        
        public static Address Zero { get; } = new Address(new byte[ByteLength]);
        public static Address SystemUser { get; } = new Address("0xfffffffffffffffffffffffffffffffffffffffe");
        
        public byte[] Bytes { get; }

        public Address(Keccak keccak)
            : this(keccak.Bytes.Slice(12, ByteLength))
        {
        }
        
        public Address(in ValueKeccak keccak)
            : this(keccak.BytesAsSpan.Slice(12, ByteLength).ToArray())
        {
        }

        public byte this[int index] => Bytes[index];

        public static bool IsValidAddress(string hexString, bool allowPrefix)
        {
            if (!(hexString.Length == HexCharsCount || allowPrefix && hexString.Length == PrefixedHexCharsCount))
            {
                return false;
            }

            bool hasPrefix = hexString.Length == PrefixedHexCharsCount;
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

                if (!isHex) return false;
            }

            return true;
        }

        public Address(string hexString)
            : this(Extensions.Bytes.FromHexString(hexString))
        {
        }

        public Address(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != ByteLength)
            {
                throw new ArgumentException(
                    $"{nameof(Address)} should be {ByteLength} bytes long and is {bytes.Length} bytes long",
                    nameof(bytes));
            }

            Bytes = bytes;
        }

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

            return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public static Address FromNumber(UInt256 number)
        {
            byte[] addressBytes = new byte[20];
            number.ToBigEndian(addressBytes);
            return new Address(addressBytes);
        }

        public static Address OfContract(Address deployingAddress, UInt256 nonce)
        {
            ValueKeccak contractAddressKeccak =
                ValueKeccak.Compute(
                    Rlp.Encode(
                        Rlp.Encode(deployingAddress),
                        Rlp.Encode(nonce)).Bytes);

            return new Address(in contractAddressKeccak);
        }
        
        public static Address OfContract(Address deployingAddress, Span<byte> salt, Span<byte> initCode)
        {
            // sha3(0xff ++ msg.sender ++ salt ++ sha3(init_code)))
            Span<byte> bytes = new byte[1 + ByteLength + 32 + salt.Length];
            bytes[0] = 0xff;
            deployingAddress.Bytes.CopyTo(bytes.Slice(1, 20));
            salt.CopyTo(bytes.Slice(21, salt.Length));
            ValueKeccak.Compute(initCode).BytesAsSpan.CopyTo(bytes.Slice(21 + salt.Length, 32));
                
            ValueKeccak contractAddressKeccak = ValueKeccak.Compute(bytes);
            return new Address(in contractAddressKeccak);
        }

        public override string ToString()
        {
            return ToString(true, false);
        }

        /// <summary>
        ///     https://github.com/ethereum/EIPs/issues/55
        /// </summary>
        /// <returns></returns>
        public string ToString(bool withEip55Checksum)
        {
            return ToString(true, withEip55Checksum);
        }
        
        /// <summary>
        ///     https://github.com/ethereum/EIPs/issues/55
        /// </summary>
        /// <returns></returns>
        public string ToString(bool withZeroX, bool withEip55Checksum)
        {
            return Bytes.ToHexString(withZeroX, false, withEip55Checksum);
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

            return obj.GetType() == GetType() && Equals((Address) obj);
        }
        
        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
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