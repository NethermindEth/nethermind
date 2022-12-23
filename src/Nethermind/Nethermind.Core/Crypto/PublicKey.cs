// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto
{
    public class PublicKey : IEquatable<PublicKey>
    {
        public const int PrefixedLengthInBytes = 65;
        public const int LengthInBytes = 64;
        private Address? _address;

        private byte[]? _prefixedBytes;

        public PublicKey(string? hexString)
            : this(Core.Extensions.Bytes.FromHexString(hexString ?? throw new ArgumentNullException(nameof(hexString))))
        {
        }

        public PublicKey(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != LengthInBytes && bytes.Length != PrefixedLengthInBytes)
            {
                throw new ArgumentException($"{nameof(PublicKey)} should be {LengthInBytes} bytes long",
                    nameof(bytes));
            }

            if (bytes.Length == PrefixedLengthInBytes && bytes[0] != 0x04)
            {
                throw new ArgumentException(
                    $"Expected prefix of 0x04 for {PrefixedLengthInBytes} bytes long {nameof(PublicKey)}");
            }

            Bytes = bytes.Slice(bytes.Length - 64, 64).ToArray();
        }

        public Address Address
        {
            get
            {
                if (_address is null)
                {
                    LazyInitializer.EnsureInitialized(ref _address, ComputeAddress);
                }

                return _address;
            }
        }

        public byte[] Bytes { get; }

        public byte[] PrefixedBytes
        {
            get
            {
                if (_prefixedBytes is null)
                {
                    return LazyInitializer.EnsureInitialized(ref _prefixedBytes,
                        () => Core.Extensions.Bytes.Concat(0x04, Bytes));
                }

                return _prefixedBytes;
            }
        }

        public bool Equals(PublicKey? other)
        {
            if (other is null)
            {
                return false;
            }

            return Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        private Address ComputeAddress()
        {
            Span<byte> hash = ValueKeccak.Compute(Bytes).BytesAsSpan;
            return new Address(hash.Slice(12).ToArray());
        }

        public static Address ComputeAddress(ReadOnlySpan<byte> publicKeyBytes)
        {
            Span<byte> hash = ValueKeccak.Compute(publicKeyBytes).BytesAsSpan;
            return new Address(hash.Slice(12).ToArray());
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as PublicKey);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }

        public override string ToString()
        {
            return Bytes.ToHexString(true);
        }

        public string ToString(bool with0X)
        {
            return Bytes.ToHexString(with0X);
        }

        public string ToShortString()
        {
            string value = Bytes.ToHexString(false);
            return $"{value.Substring(0, 6)}...{value.Substring(value.Length - 6)}";
        }

        public static bool operator ==(PublicKey? a, PublicKey? b)
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

        public static bool operator !=(PublicKey? a, PublicKey? b)
        {
            return !(a == b);
        }
    }
}
