// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Decode helpers that take the cursor by value and return the advanced one.
/// </summary>
/// <remarks>
/// <see cref="RlpReader"/> is threaded through the decoders as <c>ref RlpReader</c>, which makes it
/// address-exposed and blocks struct promotion, so every <see cref="RlpReader.Position"/> touch is a
/// real 4-byte load or store. Taking the cursor as an argument and handing it back as the return value
/// keeps it in a register for a whole chain of these calls, and the reader's field is touched once at
/// each end. <see cref="RlpReader"/>'s own methods are thin wrappers over these, so the two forms
/// cannot drift.
/// </remarks>
internal static partial class RlpHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SkipLength(ReadOnlySpan<byte> data, int position)
        => position + GetPrefixLength(data[position]);

    /// <summary>Advances past <paramref name="count"/> whole items.</summary>
    public static int SkipItems(ReadOnlySpan<byte> data, int position, int count)
    {
        for (int i = 0; i < count; i++)
        {
            position += PeekNextRlpLength(data, position);
        }

        return position;
    }

    /// <summary>Reads a multi-byte length field.</summary>
    /// <returns>The position past the field.</returns>
    public static int DeserializeLength(ReadOnlySpan<byte> data, int position, int lengthOfLength, out int length)
    {
        if (lengthOfLength == 0 || (uint)lengthOfLength > 4)
        {
            ThrowInvalidLength(lengthOfLength);
        }

        length = DeserializeLengthRef(ref MemoryMarshal.GetReference(data.Slice(position, lengthOfLength)), lengthOfLength);
        return position + lengthOfLength;
    }

    /// <summary>Reads a sequence header, yielding its content length.</summary>
    /// <returns>The position of the first item in the sequence.</returns>
    public static int ReadSequenceLength(ReadOnlySpan<byte> data, int position, out int contentLength)
    {
        int prefix = data[position++];
        if (prefix < ListOffset)
        {
            ThrowUnexpectedPrefix(prefix);
        }

        if (prefix <= ShortListMaxPrefix)
        {
            contentLength = prefix - ListOffset;
            return position;
        }

        position = DeserializeLength(data, position, prefix - ShortListMaxPrefix, out contentLength);
        if (contentLength < SmallPrefixBarrier)
        {
            ThrowUnexpectedLength(contentLength);
        }

        return position;
    }

    /// <summary>Reads the prefix of the item at <paramref name="position"/>, yielding both lengths.</summary>
    /// <returns>The position of the item's content.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadPrefixAndContentLength(
        ReadOnlySpan<byte> data, int position, out int prefixLength, out int contentLength)
    {
        (prefixLength, contentLength) = PeekPrefixAndContentLength(data, position);
        return position + Math.Max(prefixLength, 1);
    }

    /// <summary>Decodes a byte string, yielding a span over <paramref name="data"/>.</summary>
    /// <returns>The position past the string.</returns>
    public static int DecodeByteArraySpan(
        ReadOnlySpan<byte> data, int position, RlpLimit? limit, int size, out ReadOnlySpan<byte> value)
    {
        int start = position;
        int prefix = data[position++];
        ReadOnlySpan<byte> singles = SingleBytes;
        if ((uint)prefix < (uint)singles.Length)
        {
            Rlp.GuardSize(actual: 1, expected: size);
            value = singles.Slice(prefix, 1);
            return position;
        }

        if (prefix is Rlp.EmptyByteArrayByte)
        {
            Rlp.GuardSize(actual: 0, expected: size);
            value = default;
            return position;
        }

        if (prefix <= ShortStringMaxPrefix)
        {
            int length = prefix - ShortStringOffset;
            Rlp.GuardLimit(length, data.Length - position, limit);
            Rlp.GuardSize(actual: length, expected: size);

            value = data.Slice(position, length);
            if (length == 1 && value[0] < 128)
            {
                ThrowNonCanonicalInteger(start);
            }

            return position + length;
        }

        return DecodeLargerByteArraySpan(data, position, prefix, limit, size, out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int DecodeLargerByteArraySpan(
        ReadOnlySpan<byte> data, int position, int prefix, RlpLimit? limit, int size, out ReadOnlySpan<byte> value)
    {
        if (prefix < ListOffset)
        {
            int lengthOfLength = prefix - ShortStringMaxPrefix;
            if (lengthOfLength > 4)
            {
                ThrowSequenceLengthTooLong();
            }

            position = DeserializeLength(data, position, lengthOfLength, out int length);
            if (length < SmallPrefixBarrier)
            {
                ThrowUnexpectedLength(length);
            }

            Rlp.GuardSize(actual: length, expected: size);
            Rlp.GuardLimit(length, data.Length - position, limit);
            value = data.Slice(position, length);
            return position + length;
        }

        ThrowUnexpectedPrefix(prefix);
        value = default;
        return position;
    }

    /// <summary>Decodes a big-endian unsigned integer of up to 8 bytes.</summary>
    /// <returns>The position past the integer.</returns>
    public static int DecodeULong(ReadOnlySpan<byte> data, int position, out ulong value)
    {
        int start = position;
        int prefix = data[position++];

        switch (prefix)
        {
            case 0:
                value = ThrowNonCanonicalInteger(start);
                return position;
            case < 128:
                value = (ulong)prefix;
                return position;
            case 128:
                value = 0;
                return position;
        }

        int length = prefix - 128;
        if (length > 8)
        {
            ThrowUnexpectedIntegerLength(start, length);
        }

        ulong result = 0ul;
        for (int i = 8; i > 0; i--)
        {
            result <<= 8;
            if (i <= length)
            {
                result |= data[position + length - i];
                if (result == 0)
                {
                    ThrowNonCanonicalInteger(start);
                }
            }
        }

        if (result < 128)
        {
            ThrowNonCanonicalInteger(start);
        }

        value = result;
        return position + length;
    }

    /// <inheritdoc cref="DecodeULong"/>
    public static int DecodeUInt(ReadOnlySpan<byte> data, int position, out uint value)
    {
        int start = position;
        int prefix = data[position++];

        switch (prefix)
        {
            case 0:
                value = ThrowNonCanonicalInteger(start);
                return position;
            case < 128:
                value = (uint)prefix;
                return position;
            case 128:
                value = 0u;
                return position;
        }

        int length = prefix - 128;
        if (length > 4)
        {
            ThrowUnexpectedIntegerLength(start, length);
        }

        uint result = 0;
        for (int i = 4; i > 0; i--)
        {
            result <<= 8;
            if (i <= length)
            {
                result |= data[position + length - i];
                if (result == 0)
                {
                    ThrowNonCanonicalInteger(start);
                }
            }
        }

        if (result < 128)
        {
            ThrowNonCanonicalInteger(start);
        }

        value = result;
        return position + length;
    }

    /// <inheritdoc cref="DecodeULong"/>
    public static int DecodeUShort(ReadOnlySpan<byte> data, int position, out ushort value)
    {
        int start = position;
        int prefix = data[position++];

        switch (prefix)
        {
            case 0:
                ThrowNonCanonicalInteger(start);
                value = 0;
                return position;
            case < 128:
                value = (ushort)prefix;
                return position;
            case 128:
                value = 0;
                return position;
        }

        int length = prefix - 128;
        if (length > 2)
        {
            ThrowUnexpectedIntegerLength(start, length);
        }

        ushort result = 0;
        for (int i = 2; i > 0; i--)
        {
            result <<= 8;
            if (i <= length)
            {
                result |= data[position + length - i];
                if (result == 0)
                {
                    ThrowNonCanonicalInteger(start);
                }
            }
        }

        if (result < 128)
        {
            ThrowNonCanonicalInteger(start);
        }

        value = result;
        return position + length;
    }

    /// <summary>Decodes a single byte value.</summary>
    /// <returns>The position past the value.</returns>
    public static int DecodeByte(ReadOnlySpan<byte> data, int position, out byte value)
    {
        byte byteValue = data[position];
        switch (byteValue)
        {
            case 0:
                ThrowNonCanonicalInteger(position);
                value = 0;
                return position;
            case < 128:
                value = byteValue;
                return position + 1;
            case 128:
                value = 0;
                return position + 1;
            case 129 when data[position + 1] < 128:
                ThrowNonCanonicalInteger(position);
                value = 0;
                return position;
            case 129:
                value = data[position + 1];
                return position + 2;
            default:
                ThrowUnexpectedByteValue(position, byteValue);
                value = 0;
                return position;
        }
    }

    /// <summary>Decodes a signed integer that must not be negative.</summary>
    /// <returns>The position past the integer.</returns>
    public static int DecodePositiveInt(ReadOnlySpan<byte> data, int position, out int value)
    {
        int start = position;
        position = DecodeUInt(data, position, out uint unsigned);
        value = (int)unsigned;
        if (value < 0)
        {
            ThrowNegativeInteger(start, value);
        }

        return position;
    }

    /// <inheritdoc cref="DecodePositiveInt"/>
    public static int DecodePositiveLong(ReadOnlySpan<byte> data, int position, out long value)
    {
        int start = position;
        position = DecodeULong(data, position, out ulong unsigned);
        value = (long)unsigned;
        if (value < 0)
        {
            ThrowNegativeInteger(start, value);
        }

        return position;
    }

    /// <summary>Decodes a big-endian unsigned integer of up to 32 bytes.</summary>
    /// <param name="length">Required byte length, or -1 to accept any canonical encoding.</param>
    /// <returns>The position past the integer.</returns>
    public static int DecodeUInt256(ReadOnlySpan<byte> data, int position, int length, out UInt256 value)
    {
        int start = position;
        if (data[position] == 0)
        {
            ThrowNonCanonicalInteger(start);
        }

        position = DecodeByteArraySpan(data, position, RlpLimit.L32, -1, out ReadOnlySpan<byte> byteSpan);
        if (byteSpan.Length > 32)
        {
            ThrowUnexpectedIntegerLength(start, byteSpan.Length);
        }

        if (length == -1)
        {
            if (byteSpan.Length > 1 && byteSpan[0] == 0)
            {
                ThrowNonCanonicalInteger(start);
            }
        }
        else if (byteSpan.Length != length)
        {
            ThrowInvalidLength(byteSpan.Length, length);
        }

        value = new UInt256(byteSpan, true);
        return position;
    }

    /// <summary>Decodes a big-endian unsigned integer into a right-aligned 32-byte word.</summary>
    /// <returns>The position past the integer.</returns>
    public static int DecodeEvmWord(ReadOnlySpan<byte> data, int position, out EvmWord value)
    {
        int start = position;
        if (data[position] == 0)
        {
            ThrowNonCanonicalInteger(start);
        }

        position = DecodeByteArraySpan(data, position, RlpLimit.L32, -1, out ReadOnlySpan<byte> byteSpan);
        if (byteSpan.Length > 32)
        {
            ThrowUnexpectedIntegerLength(start, byteSpan.Length);
        }

        if (byteSpan.Length > 1 && byteSpan[0] == 0)
        {
            ThrowNonCanonicalInteger(start);
        }

        value = default;
        Span<byte> dest = MemoryMarshal.CreateSpan(ref Unsafe.As<EvmWord, byte>(ref value), 32);
        byteSpan.CopyTo(dest.Slice(32 - byteSpan.Length));
        return position;
    }

    /// <summary>Reads the fixed-size prefix introducing a hash.</summary>
    /// <returns>The position past the prefix; <paramref name="hasValue"/> is false for an RLP null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadKeccakPrefix(ReadOnlySpan<byte> data, int position, bool allowNull, out bool hasValue)
    {
        int prefix = data[position++];
        hasValue = prefix == KeccakRlpPrefix;
        if (!hasValue && !(allowNull && prefix == Rlp.EmptyByteArrayByte))
        {
            ThrowKeccakDecode(prefix, position, data.Length);
        }

        return position;
    }

    /// <inheritdoc cref="ReadKeccakPrefix"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadAddressPrefix(ReadOnlySpan<byte> data, int position, bool allowNull, out bool hasValue)
    {
        int prefix = data[position++];
        hasValue = prefix == AddressRlpPrefix;
        if (!hasValue && !(allowNull && prefix == Rlp.EmptyByteArrayByte))
        {
            ThrowAddressDecode(prefix, position, data.Length);
        }

        return position;
    }

    /// <summary>Decodes a 32-byte hash, or an RLP null.</summary>
    /// <returns>The position past the item.</returns>
    public static int DecodeKeccakOrNull(ReadOnlySpan<byte> data, int position, out Hash256? keccak)
    {
        position = ReadKeccakPrefix(data, position, allowNull: true, out bool hasValue);
        if (!hasValue)
        {
            keccak = null;
            return position;
        }

        keccak = InternKeccak(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    /// <inheritdoc cref="DecodeKeccakOrNull"/>
    public static int DecodeValueKeccakOrNull(ReadOnlySpan<byte> data, int position, out ValueHash256? keccak)
    {
        position = ReadKeccakPrefix(data, position, allowNull: true, out bool hasValue);
        if (!hasValue)
        {
            keccak = null;
            return position;
        }

        keccak = InternValueKeccak(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    /// <summary>Decodes a 32-byte hash without interning, reporting an RLP null instead of throwing.</summary>
    /// <returns>The position past the item.</returns>
    public static int TryDecodeValueKeccak(ReadOnlySpan<byte> data, int position, out ValueHash256 keccak, out bool hasValue)
    {
        position = ReadKeccakPrefix(data, position, allowNull: true, out hasValue);
        if (!hasValue)
        {
            Unsafe.SkipInit(out keccak);
            return position;
        }

        keccak = new ValueHash256(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    /// <summary>Decodes a 20-byte address, or an RLP null.</summary>
    /// <returns>The position past the item.</returns>
    public static int DecodeAddress(ReadOnlySpan<byte> data, int position, bool allowNull, out Address? address)
    {
        position = ReadAddressPrefix(data, position, allowNull, out bool hasValue);
        if (!hasValue)
        {
            address = null;
            return position;
        }

        address = new Address(data.Slice(position, Address.Size));
        return position + Address.Size;
    }

    /// <summary>Yields the span holding a bloom, tolerating the legacy sequence form.</summary>
    /// <remarks>
    /// Some nodes send receipt blooms wrapped in a sequence rather than as a plain 256-byte string;
    /// see https://github.com/NethermindEth/nethermind/issues/113. An empty span means an RLP null.
    /// </remarks>
    /// <returns>The position past the item.</returns>
    public static int DecodeBloomSpan(ReadOnlySpan<byte> data, int position, out ReadOnlySpan<byte> bloomBytes)
    {
        if (data[position] == 249)
        {
            position += 5; // skip 249 1 2 129 127 and read 256 bytes
            bloomBytes = data.Slice(position, Bloom.ByteLength);
            return position + Bloom.ByteLength;
        }

        return DecodeByteArraySpan(data, position, RlpLimit.Bloom, -1, out bloomBytes);
    }

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowAddressDecode(int prefix, int position, int dataLength)
        => throw new RlpException(
            $"Unexpected RLP prefix of {prefix} when decoding {nameof(Address)} at position {position} in the message of length {dataLength}.");

    /// <summary>Decodes a byte string into an array, reusing the shared single-byte arrays.</summary>
    /// <returns>The position past the string.</returns>
    public static int DecodeByteArray(
        ReadOnlySpan<byte> data, int position, RlpLimit? limit, int size, out byte[] value)
    {
        position = DecodeByteArraySpan(data, position, limit, size, out ReadOnlySpan<byte> span);
        if (span.Length == 0)
        {
            value = [];
            return position;
        }

        if (span.Length == 1)
        {
            int single = span[0];
            byte[][] arrays = SingleByteArrays;
            if ((uint)single < (uint)arrays.Length)
            {
                value = arrays[single];
                return position;
            }
        }

        value = span.ToArray();
        return position;
    }

    /// <summary>Decodes a boolean.</summary>
    /// <returns>The position past the value.</returns>
    public static int DecodeBool(ReadOnlySpan<byte> data, int position, out bool value)
    {
        byte prefix = data[position++];
        switch (prefix)
        {
            case 1:
                value = true;
                return position;
            case 128:
                value = false;
                return position;
            default:
                ThrowUnexpectedBoolValue(prefix);
                value = false;
                return position;
        }
    }

    /// <summary>Decodes a hash stored without its leading zero bytes, right-aligning it.</summary>
    /// <returns>The position past the item.</returns>
    public static int DecodeZeroPrefixKeccak(ReadOnlySpan<byte> data, int position, out Hash256? keccak)
    {
        if (data[position] == Rlp.EmptyByteArrayByte)
        {
            keccak = null;
            return position + 1;
        }

        position = DecodeByteArraySpan(data, position, RlpLimit.L32, -1, out ReadOnlySpan<byte> span);
        Span<byte> bytes = stackalloc byte[Hash256.Size];
        bytes.Clear();
        span.CopyTo(bytes[(Hash256.Size - span.Length)..]);
        keccak = new Hash256(bytes);
        return position;
    }

    /// <summary>Decodes a 32-byte hash that must be present.</summary>
    /// <returns>The position past the hash.</returns>
    public static int DecodeKeccak(ReadOnlySpan<byte> data, int position, out Hash256 keccak)
    {
        int prefix = data[position++];
        if (prefix != KeccakRlpPrefix)
        {
            ThrowKeccakDecode(prefix, position, data.Length);
        }

        keccak = InternKeccak(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    /// <inheritdoc cref="DecodeKeccak"/>
    /// <remarks>An RLP null throws, matching <see cref="RlpReader.DecodeValueKeccakNonNull"/>.</remarks>
    public static int DecodeValueKeccakNonNull(ReadOnlySpan<byte> data, int position, out ValueHash256 keccak)
    {
        int prefix = data[position++];
        if (prefix != KeccakRlpPrefix)
        {
            if (prefix == Rlp.EmptyByteArrayByte)
            {
                ThrowNullDecodedValue<ValueHash256>();
            }

            ThrowKeccakDecode(prefix, position, data.Length);
        }

        keccak = InternValueKeccak(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    /// <summary>Returns the shared instance for the two hashes that dominate account payloads.</summary>
    public static Hash256 InternKeccak(ReadOnlySpan<byte> span)
        => span.SequenceEqual(Keccak.OfAnEmptyString.Bytes) ? Keccak.OfAnEmptyString
            : span.SequenceEqual(Keccak.EmptyTreeHash.Bytes) ? Keccak.EmptyTreeHash
            : new Hash256(span);

    /// <inheritdoc cref="InternKeccak"/>
    public static ValueHash256 InternValueKeccak(ReadOnlySpan<byte> span)
        => span.SequenceEqual(Keccak.OfAnEmptyString.Bytes) ? Keccak.OfAnEmptyString.ValueHash256
            : span.SequenceEqual(Keccak.EmptyTreeHash.Bytes) ? Keccak.EmptyTreeHash.ValueHash256
            : new ValueHash256(span);

    [DoesNotReturn, StackTraceHidden]
    public static T ThrowNullDecodedValue<T>() => throw new RlpException($"{typeof(T).Name} decoded as null");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowKeccakDecode(int prefix, int position, int dataLength)
        => throw new DecodeKeccakRlpException(prefix, position, dataLength);

    // Used to avoid allocating detailed error strings on receipt fallback decode paths.
    private sealed class DecodeKeccakRlpException(int prefix, int position, int dataLength) : RlpException(string.Empty)
    {
        private string? _message;

        public override string Message => _message ??= ConstructMessage();

        private string ConstructMessage() => $"Unexpected prefix of {prefix} when decoding {nameof(Hash256)} at position {position} in the message of length {dataLength}.";
    }
}
