// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core
{
    [JsonConverter(typeof(AddressConverter))]
    [TypeConverter(typeof(AddressTypeConverter))]
    [DebuggerDisplay("{ToString()}")]
    public sealed class Address : IEquatable<Address>, IComparable<Address>
    {
        public static GenericEqualityComparer<Address> EqualityComparer { get; } = new();
        public const int Size = 20;
        private const int HexCharsCount = 2 * Size; // 5a4eab120fb44eb6684e5e32785702ff45ea344d
        private const int PrefixedHexCharsCount = 2 + HexCharsCount; // 0x5a4eab120fb44eb6684e5e32785702ff45ea344d

        public static Address Zero { get; } = new(default(ValueAddress));
        public static Address MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffff");

        public const string SystemUserHex = "0xfffffffffffffffffffffffffffffffffffffffe";
        public static Address SystemUser { get; } = new(SystemUserHex);

        private readonly ValueAddress _bytes;

        public ReadOnlySpan<byte> Bytes
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bytes.AsSpan;
        }

        private ref readonly byte FirstByte
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref MemoryMarshal.GetReference(_bytes.AsSpan);
        }

        public Address(Hash256 hash) : this(hash.Bytes.Slice(12, Size)) { }

        public Address(in ValueHash256 hash) : this(hash.BytesAsSpan.Slice(12, Size)) { }

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
                bool isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
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
                        span = span[^size..];
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

        public Address(scoped ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != Size)
            {
                throw new ArgumentException(
                    $"{nameof(Address)} should be {Size} bytes long and is {bytes.Length} bytes long",
                    nameof(bytes));
            }

            _bytes = new ValueAddress(bytes);
        }

        internal Address(in ValueAddress bytes) => _bytes = bytes;

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
            ref byte bytes0 = ref Unsafe.AsRef(in FirstByte);
            ref byte bytes1 = ref Unsafe.AsRef(in other.FirstByte);
#if ZK_EVM
            // RISC-V has no SIMD, so a Vector128 compare lowers to a slow software
            // helper. Compare the 20 bytes as two ulongs plus a uint instead.
            return Unsafe.ReadUnaligned<ulong>(ref bytes0)
                       == Unsafe.ReadUnaligned<ulong>(ref bytes1)
                && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes0, 8))
                       == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes1, 8))
                && Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes0, 16))
                       == Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes1, 16));
#else
            // Compare first 16 bytes with Vector128 and last 4 bytes with uint
            return
                Unsafe.As<byte, Vector128<byte>>(ref bytes0) ==
                Unsafe.As<byte, Vector128<byte>>(ref bytes1) &&
                Unsafe.As<byte, uint>(ref Unsafe.Add(ref bytes0, Vector128<byte>.Count)) ==
                Unsafe.As<byte, uint>(ref Unsafe.Add(ref bytes1, Vector128<byte>.Count));
#endif
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

        public override int GetHashCode() =>
#if ZK_EVM
                // Always 20 bytes, so skip the length-dispatching FastHash and use the
                // dedicated 20-byte hasher — the dominant Dictionary/FrozenSet probe on zkVM.
                unchecked((int)GetHashCode64());
#else
                Bytes.FastHash();
#endif

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

        public int CompareTo(Address? other) => other is null ? 1 : Bytes.SequenceCompareTo(other.Bytes);

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
            ref byte value = ref Unsafe.AsRef(in FirstByte);
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

        internal long GetHashCode64() => SpanExtensions.FastHash64For20Bytes(ref Unsafe.AsRef(in FirstByte));

#if ZK_EVM
        // A precompile lives at a low address (top 16 bytes zero), so its trailing number
        // IS the membership key. Returns that number when the top 16 bytes are zero, or -1
        // otherwise — lets IReleaseSpec.IsPrecompile swap a FrozenSet hash+probe for a bitmask.
        public int PrecompileIndexOrNegative()
        {
            ref byte b = ref Unsafe.AsRef(in FirstByte);
            if ((Unsafe.ReadUnaligned<ulong>(ref b) | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8))) != 0)
            {
                return -1;
            }

            // bytes 16..19, big-endian
            uint tail = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16));
            return (int)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(tail);
        }
#endif
    }

    public readonly struct AddressByEip55ChecksumOrdinalComparer : IComparer<Address>
    {
        [SkipLocalsInit]
        public int Compare(Address? a, Address? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a is null) return -1;
            if (b is null) return 1;

            Span<byte> aLowerHex = stackalloc byte[Address.Size * 2];
            Span<byte> bLowerHex = stackalloc byte[Address.Size * 2];
            a.Bytes.OutputBytesToByteHex(aLowerHex, extraNibble: false);
            b.Bytes.OutputBytesToByteHex(bLowerHex, extraNibble: false);

            ValueHash256 aChecksum = ValueKeccak.Compute(aLowerHex);
            ValueHash256 bChecksum = ValueKeccak.Compute(bLowerHex);
            for (int i = 0; i < aLowerHex.Length; i++)
            {
                char aChar = Bytes.ToChecksummedHexChar(aLowerHex[i], Bytes.GetChecksumNibble(in aChecksum, i));
                char bChar = Bytes.ToChecksummedHexChar(bLowerHex[i], Bytes.GetChecksumNibble(in bChecksum, i));
                if (aChar != bChar)
                {
                    return aChar - bChar;
                }
            }

            return 0;
        }
    }

    [JsonConverter(typeof(AddressAsKeyConverter))]
    public readonly struct AddressAsKey(Address key) : IEquatable<AddressAsKey>, IHash64bit<AddressAsKey>
    {
        public static GenericEqualityComparer<AddressAsKey> EqualityComparer { get; } = new();
        private readonly Address _key = key;
        public Address Value => _key;

        public static implicit operator Address(AddressAsKey key) => key._key;
        public static implicit operator AddressAsKey(Address key) => new(key);

        public bool Equals(AddressAsKey other) => _key == other._key;
        public override int GetHashCode() => _key?.GetHashCode() ?? 0;
        public override string ToString() => _key?.ToString() ?? "<null>";

        public long GetHashCode64() => _key?.GetHashCode64() ?? 0;

        public bool Equals(in AddressAsKey other) => _key == other._key;
    }

    public ref struct AddressStructRef
    {
        public const int ByteLength = 20;
        private const int HexCharsCount = 2 * ByteLength; // 5a4eab120fb44eb6684e5e32785702ff45ea344d
        private const int PrefixedHexCharsCount = 2 + HexCharsCount; // 0x5a4eab120fb44eb6684e5e32785702ff45ea344d

        public ReadOnlySpan<byte> Bytes { get; }

        public AddressStructRef(Hash256StructRef keccak) : this(keccak.Bytes.Slice(12, ByteLength)) { }

        public AddressStructRef(in ValueHash256 keccak) : this(keccak.BytesAsSpan.Slice(12, ByteLength)) { }

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

        public readonly Address ToAddress() => new(Bytes);
    }
}
