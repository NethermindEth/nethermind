// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using System.Threading;

using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.Crypto
{
    [JsonConverter(typeof(PublicKeyConverter))]
    public class PublicKey : IEquatable<PublicKey>
    {
        public const int PrefixedLengthInBytes = 65;
        public const int LengthInBytes = 64;
        private Address? _address;
        private Hash256? _hash256;

        private byte[]? _prefixedBytes;
        private readonly int _hashCode;

        public PublicKey(string? hexString)
            : this(Core.Extensions.Bytes.FromHexString(hexString ?? throw new ArgumentNullException(nameof(hexString))))
        {
        }

        public PublicKey(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != LengthInBytes && bytes.Length != PrefixedLengthInBytes)
            {
                throw new ArgumentException($"{nameof(PublicKey)} should be {LengthInBytes} bytes long", nameof(bytes));
            }

            if (bytes.Length == PrefixedLengthInBytes && bytes[0] != 0x04)
            {
                throw new ArgumentException($"Expected prefix of 0x04 for {PrefixedLengthInBytes} bytes long {nameof(PublicKey)}");
            }

            Bytes = bytes.Slice(bytes.Length - 64, 64).ToArray();
            _hashCode = GetHashCode(Bytes);
        }

        public Address Address
        {
            get
            {
                if (_address is null)
                {
                    LazyInitializer.EnsureInitialized(ref _address, () => new Address(Hash.Bytes[12..].ToArray()));
                }

                return _address;
            }
        }

        public Hash256 Hash
        {
            get
            {
                if (_hash256 is null)
                {
                    LazyInitializer.EnsureInitialized(ref _hash256, () => Keccak.Compute(Bytes));
                }

                return _hash256;
            }
        }

        public byte[] Bytes { get; }

        public byte[] PrefixedBytes => _prefixedBytes ??= Core.Extensions.Bytes.Concat(0x04, Bytes);

        public bool Equals(PublicKey? other) => other is not null && Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);

        public static Address ComputeAddress(ReadOnlySpan<byte> publicKeyBytes)
        {
            Span<byte> hash = ValueKeccak.Compute(publicKeyBytes).BytesAsSpan;
            return new Address(hash[12..].ToArray());
        }

        public override bool Equals(object? obj) => Equals(obj as PublicKey);

        public override int GetHashCode() => _hashCode;

        private static int GetHashCode(byte[] bytes) => new ReadOnlySpan<byte>(bytes).FastHash();

        public override string ToString() => Bytes.ToHexString(true);

        public string ToString(bool with0X) => Bytes.ToHexString(with0X);

        public string ToShortString()
        {
            string value = Bytes.ToHexString(false);
            return $"{value[..6]}...{value[^6..]}";
        }

        public static bool operator ==(PublicKey? a, PublicKey? b) =>
            a is null
                ? b is null
                : b is not null && Core.Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);

        public static bool operator !=(PublicKey? a, PublicKey? b) => !(a == b);
    }

    public readonly struct PublicKeyAsKey(PublicKey key) : IEquatable<PublicKeyAsKey>
    {
        private readonly PublicKey _key = key;
        public PublicKey Value => _key;

        public static implicit operator PublicKey(PublicKeyAsKey key) => key._key;
        public static implicit operator PublicKeyAsKey(PublicKey key) => new(key);

        public bool Equals(PublicKeyAsKey other) => _key.Equals(other._key);
        public override int GetHashCode() => _key.GetHashCode();
    }
}
