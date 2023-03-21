// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core;
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
        public const int ByteLength = 20;
        private const int HexCharsCount = 2 * ByteLength; // 5a4eab120fb44eb6684e5e32785702ff45ea344d
        private const int PrefixedHexCharsCount = 2 + HexCharsCount; // 0x5a4eab120fb44eb6684e5e32785702ff45ea344d

        public static Address Zero { get; } = new(new byte[ByteLength]);
        public static Address SystemUser { get; } = new("0xfffffffffffffffffffffffffffffffffffffffe");

        public byte[] Bytes { get; }

        public Address(Keccak keccak) : this(keccak.Bytes.Slice(12, ByteLength)) { }

        public Address(in ValueKeccak keccak) : this(keccak.BytesAsSpan.Slice(12, ByteLength).ToArray()) { }

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

        public static bool TryParse(string? value, out Address? address)
        {
            if (value is not null)
            {
                try
                {
                    byte[] bytes = Extensions.Bytes.FromHexString(value);
                    if (bytes?.Length == ByteLength)
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

        public Address(byte[] bytes)
        {
            if (bytes is null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != ByteLength)
            {
                throw new ArgumentException(
                    $"{nameof(Address)} should be {ByteLength} bytes long and is {bytes.Length} bytes long",
                    nameof(bytes));
            }

            Bytes = bytes;
        }

        public bool Equals(Address? other)
        {
            if (ReferenceEquals(null, other))
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
            if (ReferenceEquals(null, obj))
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
            long l0 = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetArrayDataReference(Bytes));
            long l1 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long)));
            int i2 = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 2));
            l0 ^= l1 ^ i2;
            return (int)(l0 ^ (l0 >> 32));
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
    }

    public ref struct AddressStructRef
    {
        public const int ByteLength = 20;
        private const int HexCharsCount = 2 * ByteLength; // 5a4eab120fb44eb6684e5e32785702ff45ea344d
        private const int PrefixedHexCharsCount = 2 + HexCharsCount; // 0x5a4eab120fb44eb6684e5e32785702ff45ea344d

        public Span<byte> Bytes { get; }

        public AddressStructRef(KeccakStructRef keccak) : this(keccak.Bytes.Slice(12, ByteLength)) { }

        public AddressStructRef(in ValueKeccak keccak) : this(keccak.BytesAsSpan.Slice(12, ByteLength).ToArray()) { }

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

        public AddressStructRef(string hexString) : this(Extensions.Bytes.FromHexString(hexString)) { }

        public AddressStructRef(Span<byte> bytes)
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

        public bool Equals(Address? other) => !ReferenceEquals(null, other) && Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);

        public bool Equals(AddressStructRef other) => Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            return obj.GetType() == typeof(Address) && Equals((Address)obj);
        }

        public override int GetHashCode() => MemoryMarshal.Read<int>(Bytes);

        public static bool operator ==(AddressStructRef a, Address? b) => a.Equals(b);

        public static bool operator !=(AddressStructRef a, Address? b) => !(a == b);

        public static bool operator ==(Address? a, AddressStructRef b) => b.Equals(a);

        public static bool operator !=(Address? a, AddressStructRef b) => !(a == b);

        public static bool operator ==(AddressStructRef a, AddressStructRef b) => a.Equals(b);

        public static bool operator !=(AddressStructRef a, AddressStructRef b) => !(a == b);

        public Address ToAddress() => new(Bytes.ToArray());
    }
}

namespace Nethermind.Serialization.Json
{
    public class AddressConverter : JsonConverter<Address>
    {
        public override Address? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            byte[]? bytes = ByteArrayConverter.Convert(ref reader);
            return bytes is null ? null : new Address(bytes);
        }

        public override void Write(
            Utf8JsonWriter writer,
            Address address,
            JsonSerializerOptions options)
        {
            ByteArrayConverter.Convert(writer, address.Bytes, skipLeadingZeros: false);
        }
    }
}
