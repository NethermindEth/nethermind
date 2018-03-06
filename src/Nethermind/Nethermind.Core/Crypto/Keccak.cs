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
using System.Threading;
using Nethermind.Core.Encoding;
using Nethermind.HashLib;

namespace Nethermind.Core.Crypto
{
    [DebuggerStepThrough]
    public struct Keccak : IEquatable<Keccak>
    {
        private const int Size = 32;

        [ThreadStatic] private static HashLib.Crypto.SHA3.Keccak256 _hash;

        public Keccak(Hex hex)
        {
            if (hex.ByteLength != Size)
            {
                throw new ArgumentException($"{nameof(Keccak)} must be {Size} bytes", nameof(hex));
            }

            Bytes = hex;
        }

        public Keccak(byte[] bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Keccak)} must be {Size} bytes", nameof(bytes));
            }

            Bytes = bytes;
        }

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly Keccak OfAnEmptyString = InternalCompute(new byte[] { });

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly Keccak OfAnEmptySequenceRlp = InternalCompute(new byte[] {192});

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static Keccak EmptyTreeHash = InternalCompute(new byte[] {128});

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Keccak Zero { get; } = new Keccak(new byte[Size]);

        public byte[] Bytes { get; }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToString(bool withZeroX)
        {
            if (Bytes == null)
            {
                return "Keccak<uninitialized>";
            }

            return Hex.FromBytes(Bytes, withZeroX);
        }

        [DebuggerStepThrough]
        public static Keccak Compute(Rlp rlp)
        {
            return InternalCompute(rlp.Bytes);
        }

        [DebuggerStepThrough]
        public static Keccak Compute(byte[] input)
        {
            if (input == null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return InternalCompute(input);
        }

        private static HashLib.Crypto.SHA3.Keccak256 Init()
        {
            return HashFactory.Crypto.SHA3.CreateKeccak256();
        }
        
        private static Keccak InternalCompute(byte[] input)
        {
            LazyInitializer.EnsureInitialized(ref _hash, Init);
            return new Keccak(_hash.ComputeBytes(input).GetBytes());
        }

        [DebuggerStepThrough]
        public static Keccak Compute(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return OfAnEmptyString;
            }

            return InternalCompute(System.Text.Encoding.UTF8.GetBytes(input));
        }

        public bool Equals(Keccak other)
        {
            return Extensions.Bytes.UnsafeCompare(other.Bytes, Bytes);
        }

        public override bool Equals(object obj)
        {
            return obj?.GetType() == typeof(Keccak) && Equals((Keccak)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                const int p = 16777619;
                int hash = (int)2166136261;

                hash = hash ^ Bytes[0] * p;
                hash = hash ^ Bytes[Size / 2] * p;
                hash = hash ^ Bytes[31] * p;
                return hash;
            }
        }

        public static bool operator ==(Keccak a, Keccak b)
        {
            return Extensions.Bytes.UnsafeCompare(a.Bytes, b.Bytes);
        }

        public static bool operator !=(Keccak a, Keccak b)
        {
            return !(a == b);
        }
    }
}