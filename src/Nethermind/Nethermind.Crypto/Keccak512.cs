//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Buffers.Binary;
using System.Threading;
using Nethermind.HashLib;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto
{
    // TODO: it is a copy-paste from Keccak, consider later a similar structure to Hashlib but compare the perf first
    public struct Keccak512 : IEquatable<Keccak512>
    {
        public const int Size = 64;

        public Keccak512(byte[] bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException($"{nameof(Keccak512)} must be {Size} bytes", nameof(bytes));
            }

            Bytes = bytes;
        }

        public static Keccak512 OfAnEmptyString = InternalCompute(new byte[] { });

        /// <returns>
        ///     <string>0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Keccak512 Zero { get; } = new(new byte[Size]);

        public byte[] Bytes { get; }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToString(bool withZeroX)
        {
            if (Bytes is null)
            {
                return "Keccak<uninitialized>";
            }

            return Core.Extensions.Bytes.ToHexString(Bytes, withZeroX);
        }

        public static Keccak512 Compute(Rlp rlp)
        {
            return InternalCompute(rlp.Bytes);
        }

        public static Keccak512 Compute(byte[] input)
        {
            if (input is null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return InternalCompute(input);
        }

        [ThreadStatic] private static HashLib.Crypto.SHA3.Keccak512 _hash;
        
        public static uint[] ComputeToUInts(byte[] input)
        {
            if (input is null || input.Length == 0)
            {
                throw new NotSupportedException();
            }

            if (_hash is null) // avoid allocating Init func
            {
                LazyInitializer.EnsureInitialized(ref _hash, Init);
            }

            return _hash.ComputeBytesToUint(input);
        }

        public static uint[] ComputeUIntsToUInts(Span<uint> input)
        {
            if (input.Length == 0)
            {
                throw new NotSupportedException();
            }

            if (_hash is null)
            {
                LazyInitializer.EnsureInitialized(ref _hash, Init);
            }

            return _hash.ComputeUIntsToUint(input);
        }
        
        public static void ComputeUIntsToUInts(Span<uint> input, Span<uint> output)
        {
            if (input.Length == 0)
            {
                throw new NotSupportedException();
            }

            if (_hash is null)
            {
                LazyInitializer.EnsureInitialized(ref _hash, Init);
            }

            _hash.ComputeUIntsToUint(input, output);
        }

        private static HashLib.Crypto.SHA3.Keccak512 Init()
        {
            return HashFactory.Crypto.SHA3.CreateKeccak512();
        }
        
        private static Keccak512 InternalCompute(byte[] input)
        {
            if (_hash is null)
            {
                LazyInitializer.EnsureInitialized(ref _hash, Init);
            }

            return new Keccak512(_hash.ComputeBytes(input).GetBytes());
        }

        public static Keccak512 Compute(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return OfAnEmptyString;
            }

            return InternalCompute(System.Text.Encoding.UTF8.GetBytes(input));
        }

        public bool Equals(Keccak512 other)
        {
            return Core.Extensions.Bytes.AreEqual(other.Bytes, Bytes);
        }

        public override bool Equals(object obj)
        {
            return obj?.GetType() == typeof(Keccak512) && Equals((Keccak512)obj);
        }

        public override int GetHashCode()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(Bytes);
        }

        public static bool operator ==(Keccak512 a, Keccak512 b)
        {
            return Core.Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator !=(Keccak512 a, Keccak512 b)
        {
            return !(a == b);
        }
    }
}
