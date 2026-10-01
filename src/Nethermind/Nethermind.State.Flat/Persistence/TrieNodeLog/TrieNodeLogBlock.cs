// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// On-disk unit of a generation file: up to <see cref="Size"/> bytes of records behind a <c>u16</c> length.
/// Records never straddle blocks and a block holds records of one batch only (a commit record closes it), so a
/// block has a single version and the index can point at blocks rather than records.
/// </summary>
internal static class TrieNodeLogBlock
{
    public const int Size = 16384;
    public const int HeaderLength = 2;
    public const int MaxStoredLength = HeaderLength + Size;

    /// <summary>Writes <paramref name="raw"/> as one block; returns the bytes written.</summary>
    public static int Write(Span<byte> destination, ReadOnlySpan<byte> raw)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)raw.Length);
        raw.CopyTo(destination[HeaderLength..]);
        return HeaderLength + raw.Length;
    }

    /// <summary>Total stored length of the block whose header starts <paramref name="header"/>, or -1 when the header is implausible (e.g. a zero-filled tail).</summary>
    public static int StoredLength(ReadOnlySpan<byte> header)
    {
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        return length == 0 || length > Size ? -1 : HeaderLength + length;
    }

    /// <summary>Copies the block at the start of <paramref name="stored"/> into <paramref name="raw"/>; returns its length, or -1 when the block is implausible or incomplete.</summary>
    public static int Read(ReadOnlySpan<byte> stored, Span<byte> raw)
    {
        if (stored.Length < HeaderLength) return -1;
        int total = StoredLength(stored);
        if (total < 0 || stored.Length < total) return -1;

        stored.Slice(HeaderLength, total - HeaderLength).CopyTo(raw);
        return total - HeaderLength;
    }

    /// <summary>Reads the record header at <paramref name="position"/> of a block; false at the end or on an implausible header.</summary>
    public static bool TryReadRecord(ReadOnlySpan<byte> raw, int position, out TrieNodeLogRecord header)
    {
        if (position + TrieNodeLogRecord.HeaderLength > raw.Length)
        {
            header = default;
            return false;
        }

        header = TrieNodeLogRecord.Read(raw[position..]);
        return header.IsPlausible && position + header.Length <= raw.Length;
    }

    /// <summary>Start of the live (not superseded) record of <paramref name="key"/> in a block, or -1.</summary>
    public static int FindRecord(ReadOnlySpan<byte> raw, byte column, ReadOnlySpan<byte> key, out TrieNodeLogRecord header)
    {
        for (int position = 0; TryReadRecord(raw, position, out header); position += header.Length)
        {
            if (header.Type is TrieNodeLogRecord.Put or TrieNodeLogRecord.Delete && header.Column == column && header.KeyLength == key.Length
                && raw.Slice(position + TrieNodeLogRecord.HeaderLength, key.Length).SequenceEqual(key))
                return position;
        }

        return -1;
    }
}
