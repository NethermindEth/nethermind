// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

/// <summary>Writes Ethereum values to a <see cref="RlpWriter"/>.</summary>
/// <remarks>
/// <para>
/// The bytes are the same as <see cref="Rlp.Encode(ReadOnlySpan{byte})"/> and the other <c>Rlp.Encode</c> helpers.
/// A <see langword="null"/> hash, address or bloom writes the empty byte string (<c>0x80</c>), and
/// <see cref="EncodeNullObject"/> writes the empty sequence (<c>0xc0</c>).
/// </para>
/// <para>
/// <see cref="RlpWriter.Write(ReadOnlySpan{byte})"/> writes a byte string. To copy bytes that are already RLP, use
/// <see cref="RlpWriter.WriteEncoded"/> or <see cref="WriteByte"/>.
/// </para>
/// </remarks>
public static class RlpWriterExtensions
{
    private static ReadOnlySpan<byte> EmptyByteArray => [Rlp.EmptyByteArrayByte];
    private static ReadOnlySpan<byte> EmptyList => [Rlp.EmptyListByte];

    /// <summary>Writes one byte as it is, without an RLP prefix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteByte(this ref RlpWriter writer, byte value) => writer.WriteEncoded(new ReadOnlySpan<byte>(in value));

    /// <summary>Writes the empty sequence (<c>0xc0</c>), which marks a missing object.</summary>
    public static void EncodeNullObject(this ref RlpWriter writer) => writer.WriteEncoded(EmptyList);

    /// <summary>Writes the empty byte string (<c>0x80</c>).</summary>
    public static void EncodeEmptyByteArray(this ref RlpWriter writer) => writer.WriteEncoded(EmptyByteArray);

    /// <summary>Writes the prefix of a byte string of <paramref name="contentLength"/> bytes; write the bytes with <see cref="RlpWriter.WriteEncoded"/>.</summary>
    /// <param name="firstByteLessThan128">Whether the content is one byte below <c>0x80</c>, which has no prefix.</param>
    public static void StartByteArray(this ref RlpWriter writer, int contentLength, bool firstByteLessThan128)
    {
        if (contentLength == 1 && firstByteLessThan128) return;

        Span<byte> prefix = stackalloc byte[1 + sizeof(int)];
        writer.WriteEncoded(prefix[..WritePrefix(prefix, Rlp.EmptyByteArrayByte, contentLength)]);
    }

    /// <summary>Writes the prefix of a sequence of <paramref name="contentLength"/> bytes, without a matching <see cref="RlpWriter.EndSequence"/>.</summary>
    /// <remarks>For code that writes nested sequences from a flat list. Prefer <see cref="RlpWriter.StartSequence"/>, which checks the length.</remarks>
    internal static void WriteSequencePrefix(this ref RlpWriter writer, int contentLength)
    {
        Span<byte> prefix = stackalloc byte[1 + sizeof(int)];
        writer.WriteEncoded(prefix[..WritePrefix(prefix, Rlp.EmptyListByte, contentLength)]);
    }

    private static int WritePrefix(Span<byte> destination, byte shortPrefix, int contentLength)
    {
        if (contentLength < 56)
        {
            destination[0] = (byte)(shortPrefix + contentLength);
            return 1;
        }

        int lengthOfLength = Rlp.LengthOfLength(contentLength);
        destination[0] = (byte)(shortPrefix + 55 + lengthOfLength);
        for (int i = lengthOfLength; i > 0; i--)
        {
            destination[i] = (byte)contentLength;
            contentLength >>= 8;
        }

        return 1 + lengthOfLength;
    }

    /// <summary>Writes a list of byte strings as a sequence; <see langword="null"/> or an empty list writes <c>0xc0</c>.</summary>
    /// <remarks>A list that is still RLP (<see cref="IRlpWrapper"/>) is copied as it is.</remarks>
    public static void WriteByteArrayList(this ref RlpWriter writer, IByteArrayList? list)
    {
        if (list is null || list.Count == 0)
        {
            writer.EncodeNullObject();
            return;
        }

        if (list is IRlpWrapper rlpWrapper)
        {
            rlpWrapper.Write(ref writer);
            return;
        }

        int contentLength = 0;
        for (int i = 0; i < list.Count; i++)
        {
            contentLength += Rlp.LengthOf(list[i]);
        }

        RlpSequence sequence = writer.StartSequence(contentLength);
        for (int i = 0; i < list.Count; i++)
        {
            writer.Write(list[i]);
        }

        writer.EndSequence(sequence);
    }

    /// <summary>Writes a 32-byte hash.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, Hash256? keccak)
    {
        if (keccak is null)
        {
            writer.EncodeEmptyByteArray();
        }
        else
        {
            writer.Write(keccak.Bytes);
        }
    }

    /// <inheritdoc cref="Encode(ref RlpWriter, Hash256)"/>
    public static void Encode(this ref RlpWriter writer, in ValueHash256? keccak)
    {
        if (keccak is null)
        {
            writer.EncodeEmptyByteArray();
        }
        else
        {
            writer.Write(keccak.Value.Bytes);
        }
    }

    /// <inheritdoc cref="Encode(ref RlpWriter, Hash256)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, in ValueHash256 keccak) => writer.Write(keccak.Bytes);

    /// <summary>Writes a sequence of 32-byte hashes; <see langword="null"/> writes <c>0xc0</c>.</summary>
    public static void Encode(this ref RlpWriter writer, Hash256[]? keccaks)
    {
        if (keccaks is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(Rlp.LengthOf(keccaks));
        for (int i = 0; i < keccaks.Length; i++)
        {
            writer.Encode(keccaks[i]);
        }

        writer.EndSequence(sequence);
    }

    /// <inheritdoc cref="Encode(ref RlpWriter, Hash256[])"/>
    public static void Encode(this ref RlpWriter writer, ValueHash256[]? keccaks)
    {
        if (keccaks is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(Rlp.LengthOf(keccaks));
        for (int i = 0; i < keccaks.Length; i++)
        {
            writer.Encode(keccaks[i]);
        }

        writer.EndSequence(sequence);
    }

    /// <inheritdoc cref="Encode(ref RlpWriter, Hash256[])"/>
    public static void Encode(this ref RlpWriter writer, IReadOnlyList<Hash256>? keccaks)
    {
        if (keccaks is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(Rlp.LengthOf(keccaks));
        int count = keccaks.Count;
        for (int i = 0; i < count; i++)
        {
            writer.Encode(keccaks[i]);
        }

        writer.EndSequence(sequence);
    }

    /// <inheritdoc cref="Encode(ref RlpWriter, Hash256[])"/>
    public static void Encode(this ref RlpWriter writer, IReadOnlyList<ValueHash256>? keccaks)
    {
        if (keccaks is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(Rlp.LengthOf(keccaks));
        int count = keccaks.Count;
        for (int i = 0; i < count; i++)
        {
            writer.Encode(keccaks[i]);
        }

        writer.EndSequence(sequence);
    }

    /// <summary>Writes a 20-byte address.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, Address? address)
    {
        if (address is null)
        {
            writer.EncodeEmptyByteArray();
        }
        else
        {
            writer.Write(address.Bytes);
        }
    }

    /// <summary>Copies an item that is already RLP; <see langword="null"/> writes <c>0x80</c>.</summary>
    public static void Encode(this ref RlpWriter writer, Rlp? rlp)
    {
        if (rlp is null)
        {
            writer.EncodeEmptyByteArray();
        }
        else
        {
            writer.WriteEncoded(rlp.Bytes);
        }
    }

    /// <summary>Writes a bloom as a 256-byte string; a zero-length bloom (EIP-7668) writes <c>0x80</c>.</summary>
    public static void Encode(this ref RlpWriter writer, Bloom? bloom)
    {
        if (bloom is null || bloom.IsZeroLength)
        {
            writer.EncodeEmptyByteArray();
        }
        else
        {
            writer.Write(bloom.Bytes);
        }
    }

    /// <summary>Writes a non-negative integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, byte value) => writer.Write(value);

    /// <summary>Writes <see langword="true"/> as <c>0x01</c> and <see langword="false"/> as <c>0x80</c>.</summary>
    public static void Encode(this ref RlpWriter writer, bool value) => writer.Write(value ? (byte)1 : (byte)0);

    /// <summary>Writes an integer; a negative value writes the 8 bytes of its two's complement.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, int value) => writer.Write(unchecked((ulong)(long)value));

    /// <summary>Writes a non-negative integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, uint value) => writer.Write((ulong)value);

    /// <inheritdoc cref="Encode(ref RlpWriter, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, long value) => writer.Write(unchecked((ulong)value));

    /// <summary>Writes a non-negative integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, ulong value) => writer.Write(value);

    /// <summary>Writes a non-negative integer of at most 32 bytes.</summary>
    /// <param name="length">
    /// The exact length of the byte string, or -1 for the canonical integer without leading zero bytes.
    /// With a length, the value keeps its leading zero bytes, as for EIP-2930 storage keys.
    /// </param>
    [SkipLocalsInit]
    public static void Encode(this ref RlpWriter writer, in UInt256 value, int length = -1)
    {
        if (length == -1 && value.IsZero)
        {
            writer.EncodeEmptyByteArray();
            return;
        }

        Span<byte> bytes = stackalloc byte[32];
        value.ToBigEndian(bytes);
        writer.Write(length == -1 ? bytes.WithoutLeadingZeros() : bytes[^length..]);
    }

    /// <summary>Writes a non-negative integer that is stored as a right-aligned big-endian word.</summary>
    public static void Encode(this ref RlpWriter writer, in EvmWord value)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref Unsafe.AsRef(in value)), 32);
        writer.WriteIntegerBytes(bytes);
    }

    /// <summary>Writes ASCII text as a byte string; <see langword="null"/> writes <c>0x80</c>.</summary>
    public static void Encode(this ref RlpWriter writer, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.EncodeEmptyByteArray();
            return;
        }

        using ArrayPoolSpan<byte> bytes = new(value.Length);
        Encoding.ASCII.GetBytes(value.AsSpan(), bytes);
        writer.Write(bytes);
    }

    /// <summary>Writes a byte string; <see langword="null"/> writes <c>0x80</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, byte[]? input) => writer.Write(input.AsSpan());

    /// <inheritdoc cref="Encode(ref RlpWriter, byte[])"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, Memory<byte>? input) => writer.Write(input.GetValueOrDefault().Span);

    /// <summary>Writes a byte string.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, in ReadOnlyMemory<byte> input) => writer.Write(input.Span);

    /// <inheritdoc cref="Encode(ref RlpWriter, in ReadOnlyMemory{byte})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(this ref RlpWriter writer, scoped ReadOnlySpan<byte> input) => writer.Write(input);

    /// <summary>Writes a sequence of byte strings.</summary>
    public static void Encode(this ref RlpWriter writer, byte[][] arrays)
    {
        int itemsLength = 0;
        foreach (byte[] array in arrays)
        {
            itemsLength += Rlp.LengthOf(array);
        }

        RlpSequence sequence = writer.StartSequence(itemsLength);
        foreach (byte[] array in arrays)
        {
            writer.Write(array);
        }

        writer.EndSequence(sequence);
    }
}
