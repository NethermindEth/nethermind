// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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

    // Pair forms of the primitives below, for call sites whose decode target is a property and so
    // cannot be an `out` argument. `(position, item.Field) = Decode…(data, position);` keeps the
    // cursor threading on one line there instead of an `out` local plus an assignment.

    /// <inheritdoc cref="DecodeULong(ReadOnlySpan{byte}, int, out ulong)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Position, ulong Value) DecodeULong(ReadOnlySpan<byte> data, int position)
        => (DecodeULong(data, position, out ulong value), value);

    /// <inheritdoc cref="DecodePositiveInt(ReadOnlySpan{byte}, int, out int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Position, int Value) DecodePositiveInt(ReadOnlySpan<byte> data, int position)
        => (DecodePositiveInt(data, position, out int value), value);

    /// <inheritdoc cref="DecodeUInt256(ReadOnlySpan{byte}, int, out UInt256, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Position, UInt256 Value) DecodeUInt256(ReadOnlySpan<byte> data, int position, int length = -1)
        => (DecodeUInt256(data, position, out UInt256 value, length), value);

    /// <inheritdoc cref="DecodeKeccak(ReadOnlySpan{byte}, int, out Hash256)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Position, Hash256 Value) DecodeKeccak(ReadOnlySpan<byte> data, int position)
        => (DecodeKeccak(data, position, out Hash256 value), value);

    /// <inheritdoc cref="DecodeKeccakOrNull(ReadOnlySpan{byte}, int, out Hash256)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Position, Hash256? Value) DecodeKeccakOrNull(ReadOnlySpan<byte> data, int position)
        => (DecodeKeccakOrNull(data, position, out Hash256? value), value);

    /// <inheritdoc cref="DecodeAddressOrNull(ReadOnlySpan{byte}, int, out Address)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Position, Address? Value) DecodeAddressOrNull(ReadOnlySpan<byte> data, int position)
        => (DecodeAddressOrNull(data, position, out Address? value), value);

    /// <inheritdoc cref="DecodeBloom(ReadOnlySpan{byte}, int, out Bloom)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Position, Bloom Value) DecodeBloom(ReadOnlySpan<byte> data, int position)
        => (DecodeBloom(data, position, out Bloom value), value);

    /// <inheritdoc cref="DecodeBloomOrNull(ReadOnlySpan{byte}, int, out Bloom)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Position, Bloom? Value) DecodeBloomOrNull(ReadOnlySpan<byte> data, int position)
        => (DecodeBloomOrNull(data, position, out Bloom? value), value);

    /// <inheritdoc cref="DecodeBloomNonNull(ReadOnlySpan{byte}, int, out Bloom)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Position, Bloom Value) DecodeBloomNonNull(ReadOnlySpan<byte> data, int position)
        => (DecodeBloomNonNull(data, position, out Bloom value), value);

    /// <inheritdoc cref="DecodeString(ReadOnlySpan{byte}, int, out string, RlpLimit)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Position, string Value) DecodeString(ReadOnlySpan<byte> data, int position, RlpLimit? limit = null)
        => (DecodeString(data, position, out string value, limit), value);

    /// <summary>Tells whether the item at <paramref name="position"/> is a sequence rather than a byte string.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSequenceNext(ReadOnlySpan<byte> data, int position) => data[position] >= ListOffset;

    /// <summary>Tells whether the item at <paramref name="position"/> is the empty sequence.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEmptySequenceNext(ReadOnlySpan<byte> data, int position) => data[position] == Rlp.EmptyListByte;

    /// <summary>Asserts that a decode finished exactly at <paramref name="expected"/>.</summary>
    /// <remarks>
    /// Takes the cursor by value so a threaded run does not have to write it back to an
    /// <see cref="RlpReader"/> just to be checked.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Check(int position, int expected)
    {
        if (position != expected)
        {
            ThrowCheckpointFailed(expected, position);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowCheckpointFailed(int expected, int position) =>
        throw new RlpException($"Data checkpoint failed. Expected {expected} and is {position}");

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        ReadOnlySpan<byte> data, int position, out ReadOnlySpan<byte> value, RlpLimit? limit = null, int size = -1)
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

#if !ZK_EVM
    // Cold path, kept out of line so the short-string case above stays small. The guest wants the
    // opposite: with no call, its byte-string decodes are 1.3M ziskemu steps cheaper.
    [MethodImpl(MethodImplOptions.NoInlining)]
#endif
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
    public static int DecodeUInt256(ReadOnlySpan<byte> data, int position, out UInt256 value, int length = -1)
    {
        int start = position;
        if (data[position] == 0)
        {
            ThrowNonCanonicalInteger(start);
        }

        position = DecodeByteArraySpan(data, position, out ReadOnlySpan<byte> byteSpan, RlpLimit.L32);
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

        value = ToUInt256(byteSpan);
        return position;
    }

    private const int UInt256Bytes = sizeof(ulong) * 4;

    /// <summary>Builds a <see cref="UInt256"/> from a big-endian RLP integer payload.</summary>
    /// <remarks>
    /// Canonical RLP integers are minimal, so a balance, fee or total difficulty is nearly always
    /// shorter than the full 32 bytes. <see cref="UInt256"/>'s constructor sends every other length
    /// through a loop that runs eight iterations per word whatever length it was handed, so those are
    /// built here instead, touching each byte once. A full-width value is left to the constructor,
    /// whose whole-word path vectorises.
    /// </remarks>
    private static UInt256 ToUInt256(ReadOnlySpan<byte> byteSpan)
    {
        int length = byteSpan.Length;
        if (length <= sizeof(ulong))
        {
            return new UInt256(Word(byteSpan, 0, length));
        }

        if (length == UInt256Bytes)
        {
            return new UInt256(byteSpan, true);
        }

        int end1 = length - sizeof(ulong);
        int end2 = end1 > sizeof(ulong) ? end1 - sizeof(ulong) : 0;
        int end3 = end2 > sizeof(ulong) ? end2 - sizeof(ulong) : 0;
        return new UInt256(
            Word(byteSpan, end1, length),
            Word(byteSpan, end2, end1),
            Word(byteSpan, end3, end2),
            Word(byteSpan, 0, end3));
    }

    /// <summary>Accumulates <paramref name="byteSpan"/> over <c>[start, end)</c> as a big-endian word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Word(ReadOnlySpan<byte> byteSpan, int start, int end)
    {
        ulong word = 0;
        for (int i = start; i < end; i++)
        {
            word = (word << 8) | byteSpan[i];
        }

        return word;
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

        position = DecodeByteArraySpan(data, position, out ReadOnlySpan<byte> byteSpan, RlpLimit.L32);
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

    // Both address decoders are force-inlined: splitting the old allowNull flag into two methods
    // otherwise leaves ILC calling them out of line from the reader wrappers, which measures as
    // +17k ziskemu steps on the guest.

    /// <summary>Decodes a 20-byte address.</summary>
    /// <returns>The position past the item.</returns>
    /// <exception cref="RlpException">The item is an RLP null or is not an address.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeAddress(ReadOnlySpan<byte> data, int position, out Address address)
    {
        position = ReadAddressPrefix(data, position, allowNull: false, out _);
        address = new Address(data.Slice(position, Address.Size));
        return position + Address.Size;
    }

    /// <summary>Decodes a 20-byte address, or an RLP null.</summary>
    /// <returns>The position past the item.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeAddressOrNull(ReadOnlySpan<byte> data, int position, out Address? address)
    {
        position = ReadAddressPrefix(data, position, allowNull: true, out bool hasValue);
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

        return DecodeByteArraySpan(data, position, out bloomBytes, RlpLimit.Bloom);
    }

    /// <summary>Decodes a bloom, interning <see cref="Bloom.Empty"/>.</summary>
    /// <returns>The position past the item.</returns>
    public static int DecodeBloomOrNull(ReadOnlySpan<byte> data, int position, out Bloom? bloom)
    {
        position = DecodeBloomSpan(data, position, out ReadOnlySpan<byte> bloomBytes);
        bloom = bloomBytes.Length == 0 ? null : CreateBloom(bloomBytes);
        return position;
    }

    /// <summary>Decodes a bloom that must be present as a plain 256-byte string.</summary>
    /// <remarks>Unlike <see cref="DecodeBloomOrNull"/> this does not accept the legacy sequence form.</remarks>
    /// <returns>The position past the item.</returns>
    /// <exception cref="RlpException">The item is not a 256-byte string.</exception>
    public static int DecodeBloom(ReadOnlySpan<byte> data, int position, out Bloom bloom)
    {
        position = DecodeByteArraySpan(data, position, out ReadOnlySpan<byte> bloomBytes, RlpLimit.Bloom, Bloom.ByteLength);
        bloom = CreateBloom(bloomBytes);
        return position;
    }

    /// <inheritdoc cref="DecodeBloomOrNull"/>
    /// <exception cref="RlpException">The item is an RLP null.</exception>
    public static int DecodeBloomNonNull(ReadOnlySpan<byte> data, int position, out Bloom bloom)
    {
        position = DecodeBloomOrNull(data, position, out Bloom? value);
        bloom = value ?? ThrowNullDecodedValue<Bloom>();
        return position;
    }

    public static Bloom CreateBloom(ReadOnlySpan<byte> bloomBytes)
    {
        if (bloomBytes.Length != Bloom.ByteLength)
        {
            throw new RlpException("Incorrect bloom RLP");
        }

        return bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? Bloom.Empty : new Bloom(bloomBytes);
    }

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowAddressDecode(int prefix, int position, int dataLength)
        => throw new RlpException(
            $"Unexpected RLP prefix of {prefix} when decoding {nameof(Address)} at position {position} in the message of length {dataLength}.");

    /// <summary>Decodes a byte string into an array, reusing the shared single-byte arrays.</summary>
    /// <returns>The position past the string.</returns>
    public static int DecodeByteArray(
        ReadOnlySpan<byte> data, int position, out byte[] value, RlpLimit? limit = null, int size = -1)
    {
        position = DecodeByteArraySpan(data, position, out ReadOnlySpan<byte> span, limit, size);
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

    /// <summary>Decodes a byte string as UTF-8 text.</summary>
    /// <returns>The position past the string.</returns>
    public static int DecodeString(ReadOnlySpan<byte> data, int position, out string value, RlpLimit? limit = null)
    {
        position = DecodeByteArraySpan(data, position, out ReadOnlySpan<byte> bytes, limit);
        value = Encoding.UTF8.GetString(bytes);
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

        position = DecodeByteArraySpan(data, position, out ReadOnlySpan<byte> span, RlpLimit.L32);
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
    {
        ulong first = FirstWord(span);
        if (first == FirstWord(Keccak.OfAnEmptyString.Bytes))
        {
            if (span.SequenceEqual(Keccak.OfAnEmptyString.Bytes)) return Keccak.OfAnEmptyString;
        }
        else if (first == FirstWord(Keccak.EmptyTreeHash.Bytes))
        {
            if (span.SequenceEqual(Keccak.EmptyTreeHash.Bytes)) return Keccak.EmptyTreeHash;
        }

        return new Hash256(span);
    }

    /// <inheritdoc cref="InternKeccak"/>
    public static ValueHash256 InternValueKeccak(ReadOnlySpan<byte> span)
    {
        ulong first = FirstWord(span);
        if (first == FirstWord(Keccak.OfAnEmptyString.Bytes))
        {
            if (span.SequenceEqual(Keccak.OfAnEmptyString.Bytes)) return Keccak.OfAnEmptyString.ValueHash256;
        }
        else if (first == FirstWord(Keccak.EmptyTreeHash.Bytes))
        {
            if (span.SequenceEqual(Keccak.EmptyTreeHash.Bytes)) return Keccak.EmptyTreeHash.ValueHash256;
        }

        return new ValueHash256(span);
    }

    /// <summary>Reads the leading 8 bytes of a hash as one word.</summary>
    /// <remarks>
    /// The interning compares are 32-byte <c>memcmp</c>s that miss for every hash but two, so the
    /// leading word discriminates first and a non-interned hash never reaches one.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FirstWord(ReadOnlySpan<byte> span)
        => Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(span));

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
