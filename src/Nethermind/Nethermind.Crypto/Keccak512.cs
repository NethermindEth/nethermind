// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Crypto;

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

        public static Keccak512 OfAnEmptyString = InternalCompute(Array.Empty<byte>());

        /// <returns>
        ///     <string>0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Keccak512 Zero { get; } = new(new byte[Size]);

        public byte[]? Bytes { get; }

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

        public static Keccak512 Compute(byte[]? input)
        {
            if (input is null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return InternalCompute(input);
        }

        public static uint[] ComputeToUInts(ReadOnlySpan<byte> input)
        {
            if (input.Length == 0)
            {
                throw new NotSupportedException();
            }

            return KeccakHash.ComputeBytesToUint(input, Size);
        }

        public static uint[] ComputeUIntsToUInts(Span<uint> input)
        {
            if (input.Length == 0)
            {
                throw new NotSupportedException();
            }

            return KeccakHash.ComputeUIntsToUint(input, Size);
        }

        public static void ComputeUIntsToUInts(Span<uint> input, Span<uint> output)
        {
            if (input.Length == 0)
            {
                throw new NotSupportedException();
            }

            KeccakHash.ComputeUIntsToUint(input, output);
        }

        private static Keccak512 InternalCompute(byte[] input)
        {
            return new Keccak512(KeccakHash.ComputeHashBytes(input, Size));
        }

        public static Keccak512 Compute(string? input)
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
