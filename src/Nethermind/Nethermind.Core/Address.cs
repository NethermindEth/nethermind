// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core
{
    [JsonConverter(typeof(AddressConverter))]
    [TypeConverter(typeof(AddressTypeConverter))]
    public class Address : IEquatable<Address>, IComparable<Address>
    {
        // Ensure that hashes are different for every run of the node and every node, so if are any hash collisions on
        // one node they will not be the same on another node or across a restart so hash collision cannot be used to degrade
        // the performance of the network as a whole.
        private static readonly uint s_instanceRandom = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

        public const int Size = 20;
        private const int HexCharsCount = 2 * Size; // 5a4eab120fb44eb6684e5e32785702ff45ea344d
        private const int PrefixedHexCharsCount = 2 + HexCharsCount; // 0x5a4eab120fb44eb6684e5e32785702ff45ea344d

        public static Address Zero { get; } = new(new byte[Size]);
        public const string SystemUserHex = "0xfffffffffffffffffffffffffffffffffffffffe";
        public static Address SystemUser { get; } = new(SystemUserHex);

        public byte[] Bytes { get; }

        public Address(Hash256 hash) : this(hash.Bytes.Slice(12, Size).ToArray()) { }

        public Address(in ValueHash256 hash) : this(hash.BytesAsSpan.Slice(12, Size).ToArray()) { }

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

        public Address(string hexString) : this(Extensions.Bytes.FromHexString(hexString)) { }

        /// <summary>
        /// Parses string value to Address. String has to be exactly 20 bytes long.
        /// </summary>
        public static bool TryParse(string? value, out Address? address)
        {
            if (value is not null)
            {
                try
                {
                    byte[] bytes = Extensions.Bytes.FromHexString(value);
                    if (bytes.Length == Size)
                    {
                        address = new Address(bytes);
                        return true;
                    }
                }
                catch (IndexOutOfRangeException) { }
            }

            address = default;
            return false;
        }

        /// <summary>
        /// Parses string value to Address. String can be shorter than 20 bytes long, it is padded with leading 0's then.
        /// </summary>
        public static bool TryParseVariableLength(string? value, out Address? address, bool allowOverflow = false)
        {
            if (value is not null)
            {
                const int size = Size << 1;

                int start = value is ['0', 'x', ..] ? 2 : 0;
                ReadOnlySpan<char> span = value.AsSpan(start);
                if (span.Length > size)
                {
                    if (allowOverflow)
                    {
                        span = span.Slice(value.Length - size);
                    }
                    else
                    {
                        goto False;
                    }
                }

                address = new Address(Extensions.Bytes.FromHexString(span, Size));
                return true;
            }

        False:
            address = default;
            return false;
        }

        public Address(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);

            if (bytes.Length != Size)
            {
                throw new ArgumentException(
                    $"{nameof(Address)} should be {Size} bytes long and is {bytes.Length} bytes long",
                    nameof(bytes));
            }

            Bytes = bytes;
        }

        public bool Equals(Address? other)
        {
            if (other is null)
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public static Address FromNumber(in UInt256 number)
        {
            byte[] addressBytes = new byte[20];
            number.ToBigEndian(addressBytes);
            return new Address(addressBytes);
        }

        public override string ToString() => ToString(true, false);

        /// <summary>
        ///     https://github.com/ethereum/EIPs/issues/55
        /// </summary>
        /// <returns></returns>
        public string ToString(bool withEip55Checksum) => ToString(true, withEip55Checksum);

        /// <summary>
        ///     https://github.com/ethereum/EIPs/issues/55
        /// </summary>
        /// <returns></returns>
        public string ToString(bool withZeroX, bool withEip55Checksum) => Bytes.ToHexString(withZeroX, false, withEip55Checksum);

        public override bool Equals(object? obj)
        {
            if (obj is null)
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            return obj.GetType() == GetType() && Equals((Address)obj);
        }

        public override int GetHashCode()
        {
            uint hash = s_instanceRandom;
            hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetArrayDataReference(Bytes)));
            hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(ulong))));
            hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 2)));
            return (int)hash;
        }

        public static bool operator ==(Address? a, Address? b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            return a?.Equals(b) ?? false;
        }

        public static bool operator !=(Address? a, Address? b) => !(a == b);

        public AddressStructRef ToStructRef() => new(Bytes);

        public int CompareTo(Address? other) => Bytes.AsSpan().SequenceCompareTo(other?.Bytes);

        private class AddressTypeConverter : TypeConverter
        {
            public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
                value is string stringValue ? new Address(stringValue) : base.ConvertFrom(context, culture, value);

            public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
                destinationType == typeof(string) && value is not null
                    ? ((Address)value).ToString()
                    : base.ConvertTo(context, culture, value, destinationType);

            public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
                sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

            public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
                destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
        }

        public Hash256 ToAccountPath => Keccak.Compute(Bytes);

        [SkipLocalsInit]
        public ValueHash256 ToHash()
        {
            Span<byte> addressBytes = stackalloc byte[Hash256.Size];
            Bytes.CopyTo(addressBytes.Slice(Hash256.Size - Address.Size));
            return new ValueHash256(addressBytes);
        }
    }

    public readonly struct AddressAsKey(Address key) : IEquatable<AddressAsKey>
    {
        private readonly Address _key = key;
        public Address Value => _key;

        public static implicit operator Address(AddressAsKey key) => key._key;
        public static implicit operator AddressAsKey(Address key) => new(key);

        public bool Equals(AddressAsKey other) => _key.Equals(other._key);
        public override int GetHashCode() => _key.GetHashCode();
    }

    public ref struct AddressStructRef
    {
        public const int ByteLength = 20;
        private const int HexCharsCount = 2 * ByteLength; // 5a4eab120fb44eb6684e5e32785702ff45ea344d
        private const int PrefixedHexCharsCount = 2 + HexCharsCount; // 0x5a4eab120fb44eb6684e5e32785702ff45ea344d

        public ReadOnlySpan<byte> Bytes { get; }

        public AddressStructRef(Hash256StructRef keccak) : this(keccak.Bytes.Slice(12, ByteLength)) { }

        public AddressStructRef(in ValueHash256 keccak) : this(keccak.BytesAsSpan.Slice(12, ByteLength).ToArray()) { }

        public readonly byte this[int index] => Bytes[index];

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

        public AddressStructRef(string hexString) : this(Extensions.Bytes.FromHexString(hexString)) { }

        public AddressStructRef(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != ByteLength)
            {
                throw new ArgumentException(
                    $"{nameof(Address)} should be {ByteLength} bytes long and is {bytes.Length} bytes long",
                    nameof(bytes));
            }

            Bytes = bytes;
        }

        public static AddressStructRef FromNumber(in UInt256 number)
        {
            byte[] addressBytes = new byte[20];
            number.ToBigEndian(addressBytes);
            return new AddressStructRef(addressBytes);
        }

        public override readonly string ToString() => ToString(true, false);

        /// <summary>
        ///     https://github.com/ethereum/EIPs/issues/55
        /// </summary>
        /// <returns></returns>
        public readonly string ToString(bool withEip55Checksum) => ToString(true, withEip55Checksum);

        /// <summary>
        ///     https://github.com/ethereum/EIPs/issues/55
        /// </summary>
        /// <returns></returns>
        public readonly string ToString(bool withZeroX, bool withEip55Checksum) => Bytes.ToHexString(withZeroX, false, withEip55Checksum);

        public readonly bool Equals(Address? other) => other is not null && Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);

        public readonly bool Equals(AddressStructRef other) => Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);

        public override readonly bool Equals(object? obj)
        {
            if (obj is null)
            {
                return false;
            }

            return obj.GetType() == typeof(Address) && Equals((Address)obj);
        }

        public override readonly int GetHashCode() => MemoryMarshal.Read<int>(Bytes);

        public static bool operator ==(AddressStructRef a, Address? b) => a.Equals(b);

        public static bool operator !=(AddressStructRef a, Address? b) => !(a == b);

        public static bool operator ==(Address? a, AddressStructRef b) => b.Equals(a);

        public static bool operator !=(Address? a, AddressStructRef b) => !(a == b);

        public static bool operator ==(AddressStructRef a, AddressStructRef b) => a.Equals(b);

        public static bool operator !=(AddressStructRef a, AddressStructRef b) => !(a == b);

        public readonly Address ToAddress() => new(Bytes.ToArray());
    }
}
