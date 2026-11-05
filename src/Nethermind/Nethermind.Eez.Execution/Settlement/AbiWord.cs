// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>Writes one 32-byte ABI word: static values right-aligned, zero-padded on the left.</summary>
internal static class AbiWord
{
    public const int Size = 32;

    public static void Write(Span<byte> word, ulong value)
    {
        word[..(Size - sizeof(ulong))].Clear();
        BinaryPrimitives.WriteUInt64BigEndian(word[(Size - sizeof(ulong))..], value);
    }

    public static void Write(Span<byte> word, bool value) => Write(word, value ? 1UL : 0UL);

    public static void Write(Span<byte> word, Address address)
    {
        word[..(Size - Address.Size)].Clear();
        address.Bytes.CopyTo(word[(Size - Address.Size)..]);
    }

    public static void Write(Span<byte> word, in UInt256 value) => value.ToBigEndian(word);

    /// <summary>The length of <paramref name="length"/> bytes padded to whole words.</summary>
    public static int PaddedLength(int length) => (length + Size - 1) / Size * Size;
}
