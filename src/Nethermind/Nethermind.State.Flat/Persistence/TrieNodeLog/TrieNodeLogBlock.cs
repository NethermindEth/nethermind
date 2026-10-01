// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// On-disk unit of a generation file: up to <see cref="Size"/> bytes of records, stored verbatim or LZ4-compressed.
/// Layout: <c>u16 rawLength | u16 storedLength | payload</c>; the payload is uncompressed when the two lengths
/// are equal. Records never straddle blocks and a block holds records of one batch only (a commit record closes
/// it), so a block has a single version and the index can point at blocks rather than records.
/// </summary>
internal static class TrieNodeLogBlock
{
    public const int Size = 8192;
    public const int HeaderLength = 2 + 2;
    public const int MaxStoredLength = HeaderLength + Size;

    /// <summary>Writes <paramref name="raw"/> as one block, LZ4-compressed when <paramref name="compress"/> and that makes it smaller; returns the bytes written.</summary>
    public static int Write(Span<byte> destination, ReadOnlySpan<byte> raw, bool compress)
    {
        // The target is capped at the raw length so a result that does not shrink fails and is stored verbatim.
        int stored = compress ? LZ4Codec.Encode(raw, destination.Slice(HeaderLength, raw.Length)) : -1;
        if (stored <= 0)
        {
            raw.CopyTo(destination[HeaderLength..]);
            stored = raw.Length;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)raw.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)stored);
        return HeaderLength + stored;
    }

    /// <summary>Total stored length of the block whose header starts <paramref name="header"/>, or -1 when the header is implausible (e.g. a zero-filled tail).</summary>
    public static int StoredLength(ReadOnlySpan<byte> header)
    {
        int rawLength = BinaryPrimitives.ReadUInt16LittleEndian(header);
        int storedLength = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        return rawLength == 0 || rawLength > Size || storedLength == 0 || storedLength > rawLength ? -1 : HeaderLength + storedLength;
    }

    /// <summary>Decodes the block at the start of <paramref name="stored"/> into <paramref name="raw"/>; returns the raw length, or -1 when the block is implausible or does not decode.</summary>
    public static int Read(ReadOnlySpan<byte> stored, Span<byte> raw)
    {
        if (stored.Length < HeaderLength) return -1;
        int total = StoredLength(stored);
        if (total < 0 || stored.Length < total) return -1;

        int rawLength = BinaryPrimitives.ReadUInt16LittleEndian(stored);
        ReadOnlySpan<byte> payload = stored.Slice(HeaderLength, total - HeaderLength);
        if (payload.Length == rawLength)
        {
            payload.CopyTo(raw);
            return rawLength;
        }

        return LZ4Codec.Decode(payload, raw[..rawLength]) == rawLength ? rawLength : -1;
    }

    /// <summary>Reads the record header at <paramref name="position"/> of a decoded block; false at the end or on an implausible header.</summary>
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

    /// <summary>Start of the live (not superseded) record of <paramref name="key"/> in a decoded block, or -1.</summary>
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
