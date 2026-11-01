// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Prestate;

/// <summary>Byte layout of a block pre-state record, version 1.</summary>
/// <remarks>
/// A fixed 128-byte header, little-endian, then the accounts strictly ascending by address. Each account is
/// <c>address(20) flags(1)</c>, then, when it exists, <c>nonce</c> as a LEB128 varint and <c>balance</c> as a length byte
/// and minimal big-endian bytes, then the code hash and the storage root when not empty, then, when it has slots, a
/// varint count and the slots strictly ascending by index, each <c>index(32) length(1) value</c> with a minimal
/// big-endian value; a zero value has length 0. Keys and values are big-endian, so byte order is numeric order.
/// </remarks>
internal static class PrestateRecordFormat
{
    public const byte Version = 1;
    public const int HeaderLength = 128;

    /// <summary>The record was captured with non-authoritative storage roots, so empty-storage reads are in it.</summary>
    public const byte RecordFlagNonAuthoritativeRoots = 1;

    public const byte AccountExists = 1;
    public const byte AccountHasCode = 2;
    public const byte AccountHasStorageRoot = 4;
    public const byte AccountHasSlots = 8;
    /// <summary>The account value was not read before a write reached it; only its slots are recorded.</summary>
    public const byte AccountValueNotRecorded = 16;
    public const byte KnownAccountFlags = AccountExists | AccountHasCode | AccountHasStorageRoot | AccountHasSlots | AccountValueNotRecorded;

    public const int VersionOffset = 0;
    public const int FlagsOffset = 1;
    public const int AccountCountOffset = 4;
    public const int SlotCountOffset = 8;
    public const int SenderCountOffset = 12;
    public const int ParentNumberOffset = 16;
    public const int ParentRootOffset = 24;
    public const int BlockHashOffset = 56;
    public const int BlockNumberOffset = 88;
    public const int ReservedOffset = 96;

    public const int MaxVarIntLength = 10;
    public const int MaxAccountLength = Address.Size + 1 + MaxVarIntLength + 1 + 32 + Hash256.Size + Hash256.Size + MaxVarIntLength;
    public const int MaxSlotLength = 32 + 1 + 32;
    /// <summary>The smallest possible encoding of each, which bounds the counts a header may claim.</summary>
    public const int MinAccountLength = Address.Size + 1;
    public const int MinSlotLength = 32 + 1;

    /// <summary>Store key of the record whose parent state is <paramref name="parent"/>: the parent number, big-endian,
    /// then its state root, so records sort by block and a prune is one range deletion.</summary>
    public const int KeyLength = 8 + Hash256.Size;

    public static void WriteKey(in StateId parent, Span<byte> key)
    {
        BinaryPrimitives.WriteUInt64BigEndian(key, parent.BlockNumber);
        parent.StateRoot.Bytes.CopyTo(key[8..]);
    }

    public static int WriteVarInt(Span<byte> destination, ulong value)
    {
        int written = 0;
        while (value >= 0x80)
        {
            destination[written++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[written++] = (byte)value;
        return written;
    }

    /// <summary>False on a truncated, overlong or non-minimal varint.</summary>
    public static bool TryReadVarInt(ReadOnlySpan<byte> source, ref int position, out ulong value)
    {
        value = 0;
        for (int shift = 0, i = 0; i < MaxVarIntLength; i++, shift += 7)
        {
            if ((uint)position >= (uint)source.Length) return false;
            byte current = source[position++];
            if (i == MaxVarIntLength - 1 && current > 1) return false;
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return current != 0 || i == 0;
        }

        return false;
    }
}
