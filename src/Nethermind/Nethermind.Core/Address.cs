// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Json.Serialization;

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core
{
    [JsonConverter(typeof(AddressConverter))]
    [TypeConverter(typeof(AddressTypeConverter))]
    [DebuggerDisplay("{ToString()}")]
    public class Address : IEquatable<Address>, IComparable<Address>
    {
        public const int Size = 20;
        private const int HexCharsCount = 2 * Size; // 5a4eab120fb44eb6684e5e32785702ff45ea344d
        private const int PrefixedHexCharsCount = 2 + HexCharsCount; // 0x5a4eab120fb44eb6684e5e32785702ff45ea344d

        public static Address Zero { get; } = new(new byte[Size]);
        public static Address MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffff");

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
                        span = span[(value.Length - size)..];
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

        public Address(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException(
                    $"{nameof(Address)} should be {Size} bytes long and is {bytes.Length} bytes long",
                    nameof(bytes));
            }

            Bytes = bytes.ToArray();
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

            // Address must be 20 bytes long Vector128 + uint
            ref byte bytes0 = ref MemoryMarshal.GetArrayDataReference(Bytes);
            ref byte bytes1 = ref MemoryMarshal.GetArrayDataReference(other.Bytes);
            // Compare first 16 bytes with Vector128 and last 4 bytes with uint
            return
                Unsafe.As<byte, Vector128<byte>>(ref bytes0) ==
                Unsafe.As<byte, Vector128<byte>>(ref bytes1) &&
                Unsafe.As<byte, uint>(ref Unsafe.Add(ref bytes0, Vector128<byte>.Count)) ==
                Unsafe.As<byte, uint>(ref Unsafe.Add(ref bytes1, Vector128<byte>.Count));
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

        public string ToShortString(bool withZeroX = true)
        {
            string address = Bytes.ToHexString(withZeroX);
            return $"{address[..(withZeroX ? 8 : 6)]}...{address[^6..]}";
        }

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

        public override int GetHashCode() => new ReadOnlySpan<byte>(Bytes).FastHash();

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

        public ValueHash256 ToAccountPath => KeccakCache.Compute(Bytes);

        [SkipLocalsInit]
        public ValueHash256 ToHash()
        {
            ref byte value = ref MemoryMarshal.GetArrayDataReference(Bytes);
            // build the 4×8-byte lanes:
            // - lane0 = 0UL
            // - lane1 = first 4 bytes of 'value', shifted up into the high half
            // - lane2 = bytes [4..11] of 'value'
            // - lane3 = bytes [12..19] of 'value'
            ulong lane1 = ((ulong)Unsafe.As<byte, uint>(ref value)) << 32;
            ulong lane2 = Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, 4));
            ulong lane3 = Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, 12));

            Unsafe.SkipInit(out ValueHash256 result);
            Unsafe.As<ValueHash256, Vector256<byte>>(ref result)
                = Vector256.Create(default, lane1, lane2, lane3).AsByte();

            return result;
        }
    }

    public readonly struct AddressAsKey(Address key) : IEquatable<AddressAsKey>
    {
        private readonly Address _key = key;
        public Address Value => _key;

        public static implicit operator Address(AddressAsKey key) => key._key;
        public static implicit operator AddressAsKey(Address key) => new(key);

        public bool Equals(AddressAsKey other) => _key == other._key;
        public override int GetHashCode() => _key?.GetHashCode() ?? 0;
        public override string ToString()
        {
            return _key?.ToString() ?? "<null>";
        }
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
