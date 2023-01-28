// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;

namespace Nethermind.Core2.Crypto
{
    public class BlsSignature : IEquatable<BlsSignature>
    {
        public const int Length = 96;

        public static readonly BlsSignature Zero = new BlsSignature(new byte[Length]);

        private BlsSignature(byte[] bytes)
        {
            Bytes = bytes;
        }

        public BlsSignature(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length,
                    $"{nameof(Root)} must have exactly {Length} bytes");
            }
            Bytes = span.ToArray();
        }

        /// <summary>
        /// Creates a BlsSignature directly using the provided buffer; it is up to the caller to ensure the buffer is unique.
        /// </summary>
        public static BlsSignature WithBuffer(byte[] bytes)
        {
            return new BlsSignature(bytes);
        }

        public byte[] Bytes { get; }

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(Bytes);
        }

        public bool Equals(BlsSignature? other)
        {
            return other != null && Core2.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((BlsSignature)obj);
        }

        public override int GetHashCode()
        {
            return Bytes != null ? BinaryPrimitives.ReadInt32LittleEndian(Bytes) : 0;
        }

        public static bool operator ==(BlsSignature? left, BlsSignature? right)
        {
            if (left is null)
            {
                return right is null;
            }

            return left.Equals(right);
        }

        public static bool operator !=(BlsSignature? left, BlsSignature? right)
        {
            return !(left == right);
        }

        public override string ToString()
        {
            return Bytes.ToHexString(true);
        }
    }
}
