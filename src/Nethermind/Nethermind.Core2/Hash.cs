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
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Core2
{
    [DebuggerStepThrough]
    public class Hash : IEquatable<Hash>
    {
        internal const int Size = 32;

        public Hash(string hexString)
            : this(Core.Extensions.Bytes.FromHexString(hexString))
        {
        }

        public Hash(byte[] bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Hash)} must be {Size} bytes and was {bytes.Length} bytes",
                    nameof(bytes));
            }

            Bytes = bytes;
        }

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly Hash OfAnEmptyString = InternalCompute(new byte[] { });

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly Hash OfAnEmptySequenceRlp = InternalCompute(new byte[] {192});

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static Hash EmptyTreeHash = InternalCompute(new byte[] {128});

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Hash Zero { get; } = new Hash(new byte[Size]);

        public byte[] Bytes { get; }

        public override string ToString()
        {
            return ToString(true);
        }
        
        public string ToShortString()
        {
            string hash = Bytes?.ToHexString(false);
            return $"{hash?.Substring(0, 6)}...{hash?.Substring(hash.Length - 6)}";
        }

        public string ToString(bool withZeroX)
        {
            if (Bytes == null)
            {
                return "Keccak<uninitialized>";
            }

            return Bytes.ToHexString(withZeroX);
        }

        [DebuggerStepThrough]
        public static Hash Compute(Rlp rlp)
        {
            return InternalCompute(rlp.Bytes);
        }

        [DebuggerStepThrough]
        public static Hash Compute(byte[] input)
        {
            if (input == null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return new Hash(KeccakHash.ComputeHashBytes(input));
        }

        [DebuggerStepThrough]
        public static Hash Compute(Span<byte> input)
        {
            if (input == null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return new Hash(KeccakHash.ComputeHashBytes(input));
        }

        private static Hash InternalCompute(byte[] input)
        {
            return new Hash(KeccakHash.ComputeHashBytes(input.AsSpan()));
        }

        [DebuggerStepThrough]
        public static Hash Compute(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return OfAnEmptyString;
            }

            return InternalCompute(System.Text.Encoding.UTF8.GetBytes(input));
        }

        public bool Equals(Hash other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return Core.Extensions.Bytes.AreEqual(other.Bytes, Bytes);
        }

        public override bool Equals(object obj)
        {
            return obj?.GetType() == typeof(Hash) && Equals((Hash) obj);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }

        public static bool operator ==(Hash a, Hash b)
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

        public static bool operator !=(Hash a, Hash b)
        {
            return !(a == b);
        }
    }
}