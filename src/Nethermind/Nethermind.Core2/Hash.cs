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
using Nethermind.HashLib;

namespace Nethermind.Core2
{
    [DebuggerStepThrough]
    public class Sha256 : IEquatable<Sha256>
    {
        private static readonly IHash Hash = HashFactory.Crypto.CreateSHA256();  

        internal const int Size = 32;

        public Sha256(string hexString)
            : this(Core.Extensions.Bytes.FromHexString(hexString))
        {
        }

        public Sha256(byte[] bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Sha256)} must be {Size} bytes and was {bytes.Length} bytes",
                    nameof(bytes));
            }

            Bytes = bytes;
        }

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly Sha256 OfAnEmptyString = InternalCompute(new byte[] { });

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly Sha256 OfAnEmptySequenceRlp = InternalCompute(new byte[] {192});

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static Sha256 EmptyTreeHash = InternalCompute(new byte[] {128});

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Sha256 Zero { get; } = new Sha256(new byte[Size]);

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
                return "Sha256<uninitialized>";
            }

            return Bytes.ToHexString(withZeroX);
        }

        [DebuggerStepThrough]
        public static Sha256 Compute(Rlp rlp)
        {
            return InternalCompute(rlp.Bytes);
        }

        [DebuggerStepThrough]
        public static Sha256 Compute(byte[] input)
        {
            if (input == null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return Compute(input.AsSpan());
        }

        [DebuggerStepThrough]
        public static Sha256 Compute(Span<byte> input)
        {
            if (input == null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return InternalCompute(input.ToArray());
        }
        
        public static void ComputeInPlace(Span<byte> input)
        {
            if (input == null || input.Length == 0)
            {
                OfAnEmptyString.Bytes.AsSpan().CopyTo(input);
            }

            byte[] bytes = Hash.ComputeBytes(input.ToArray()).GetBytes();
            bytes.AsSpan().CopyTo(input);
        }

        private static Sha256 InternalCompute(byte[] input)
        {
            return new Sha256(Hash.ComputeBytes(input).GetBytes());
        }

        [DebuggerStepThrough]
        public static Sha256 Compute(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return OfAnEmptyString;
            }

            return InternalCompute(System.Text.Encoding.UTF8.GetBytes(input));
        }

        public bool Equals(Sha256 other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return Core.Extensions.Bytes.AreEqual(other.Bytes, Bytes);
        }

        public override bool Equals(object obj)
        {
            return obj?.GetType() == typeof(Sha256) && Equals((Sha256) obj);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }

        public static bool operator ==(Sha256 a, Sha256 b)
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

        public static bool operator !=(Sha256 a, Sha256 b)
        {
            return !(a == b);
        }
    }
}