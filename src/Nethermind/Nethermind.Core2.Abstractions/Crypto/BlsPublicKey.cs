// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Core2.Crypto
{
    public class BlsPublicKey : IEquatable<BlsPublicKey>
    {
        public const int Length = 48;

        public static readonly BlsPublicKey Zero = new BlsPublicKey(new byte[Length]);

        public BlsPublicKey(string hexString)
            : this(Core2.Bytes.FromHexString(hexString))
        {
        }

        public BlsPublicKey(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentException($"{nameof(BlsPublicKey)} should be {Length} bytes long", nameof(span));
            }

            Bytes = span.ToArray();
        }

        public byte[] Bytes { get; }

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(Bytes);
        }

        public bool Equals(BlsPublicKey? other)
        {
            return !(other is null) && Core2.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as BlsPublicKey);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }

        public static bool operator ==(BlsPublicKey? a, BlsPublicKey? b)
        {
            if (ReferenceEquals(a, null))
            {
                return ReferenceEquals(b, null);
            }

            if (ReferenceEquals(b, null))
            {
                return false;
            }

            return Core2.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator !=(BlsPublicKey? a, BlsPublicKey? b)
        {
            return !(a == b);
        }

        public string ToShortString()
        {
            var value = Bytes.ToHexString(false);
            return $"{value.Substring(0, 6)}...{value.Substring(value.Length - 6)}";
            ;
        }

        public override string ToString()
        {
            return Bytes.ToHexString(true);
        }
    }
}
