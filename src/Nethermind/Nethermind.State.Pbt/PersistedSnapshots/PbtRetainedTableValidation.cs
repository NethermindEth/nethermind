// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Pbt.PersistedSnapshots;

internal static class PbtRetainedTableValidation
{
    internal static void Validate(scoped in ArenaByteReader reader)
    {
        Bound table = new(0, reader.Length);
        if (!SortedTable.TryReadFooter<ArenaByteReader, NoOpPin>(reader, table, out SortedTable.Footer footer))
            throw Corrupt();
        long index = footer.IndexOffset;
        ReadHeader(reader, index, reader.Length - SortedTable.FooterSize, 4, out long start, out long end, out long restarts);
        if (end != reader.Length - SortedTable.FooterSize) throw Corrupt();
        Span<byte> key = stackalloc byte[255];
        Span<byte> previous = stackalloc byte[255];
        Span<byte> prefix = stackalloc byte[3];
        Span<byte> offsetBytes = stackalloc byte[8];
        offsetBytes.Clear();
        int length = 0, previousLength = 0;
        long position = start, nextBlock = 0, restartIndex = 0;
        byte[]? previousSeparator = null;
        while (position < end)
        {
            Read(reader, position, prefix);
            int cp = prefix[0], suffix = prefix[1], width = prefix[2];
            if (cp > length || cp + suffix > 255 || width > 6 || position + 3 + suffix + width > end) throw Corrupt();
            CheckRestart(reader, index, 4, restarts, position, cp, ref restartIndex);
            Read(reader, position + 3, key.Slice(cp, suffix));
            length = cp + suffix;
            if (length == 0 || previousLength != 0 && previous[..previousLength].SequenceCompareTo(key[..length]) >= 0) throw Corrupt();
            if (cp == 0) offsetBytes.Clear();
            Read(reader, position + 3 + suffix, offsetBytes[..width]);
            long block = BinaryPrimitives.ReadInt64LittleEndian(offsetBytes);
            if (block != nextBlock || block >= index) throw Corrupt();
            byte[] last = ValidateData(reader, block, index, previousSeparator, out long dataEnd);
            if (last.AsSpan().SequenceCompareTo(key[..length]) > 0) throw Corrupt();
            nextBlock = (dataEnd + SortedTable.BlockSize - 1) & ~(SortedTable.BlockSize - 1L);
            previousSeparator = key[..length].ToArray();
            key[..length].CopyTo(previous);
            previousLength = length;
            position += 3 + suffix + width;
            if (position == end && dataEnd != index) throw Corrupt();
        }
        if (position != end || restartIndex != restarts || index > 0 && previousLength == 0) throw Corrupt();
    }

    private static byte[] ValidateData(scoped in ArenaByteReader reader, long block, long limit,
        byte[]? previousSeparator, out long end)
    {
        ReadHeader(reader, block, Math.Min(limit, block + SortedTable.BlockSize), 2, out long start, out end, out long restarts);
        Span<byte> key = stackalloc byte[255];
        Span<byte> previous = stackalloc byte[255];
        Span<byte> header = stackalloc byte[3];
        int length = 0, previousLength = 0;
        long position = start, restartIndex = 0;
        while (position < end)
        {
            Read(reader, position, header);
            int cp = header[0], suffix = header[1], value = header[2];
            if (cp > length || cp + suffix > 255 || position + 3 + suffix + value > end) throw Corrupt();
            CheckRestart(reader, block, 2, restarts, position, cp, ref restartIndex);
            Read(reader, position + 3, key.Slice(cp, suffix));
            length = cp + suffix;
            if (length == 0 || previousLength > 0 && previous[..previousLength].SequenceCompareTo(key[..length]) >= 0
                || previousLength == 0 && previousSeparator is not null && previousSeparator.AsSpan().SequenceCompareTo(key[..length]) >= 0)
                throw Corrupt();
            key[..length].CopyTo(previous);
            previousLength = length;
            position += 3 + suffix + value;
        }
        if (position != end || restartIndex != restarts || length == 0) throw Corrupt();
        return key[..length].ToArray();
    }

    private static void ReadHeader(scoped in ArenaByteReader reader, long block, long limit, int width,
        out long start, out long end, out long restarts)
    {
        Span<byte> bytes = stackalloc byte[9];
        Read(reader, block, bytes[..(1 + 2 * width)]);
        if (bytes[0] != (width == 2 ? Block.FlagBlock : Block.FlagIndex)) throw Corrupt();
        long relativeEnd = width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[1..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]);
        restarts = width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[3..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]);
        start = checked(block + 1 + 2 * width + restarts * width);
        end = checked(block + relativeEnd);
        if (block < 0 || start > end || end > limit || start != end && restarts == 0) throw Corrupt();
    }

    private static void CheckRestart(scoped in ArenaByteReader reader, long block, int width, long count,
        long position, int commonPrefix, ref long restartIndex)
    {
        if (commonPrefix != 0) return;
        if (restartIndex >= count) throw Corrupt();
        Span<byte> bytes = stackalloc byte[4];
        Read(reader, block + 1 + 2 * width + restartIndex * width, bytes[..width]);
        long offset = width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (offset != position - block) throw Corrupt();
        restartIndex++;
    }

    internal static void Read(scoped in ArenaByteReader reader, long offset, scoped Span<byte> destination)
    {
        if (offset < 0 || offset > reader.Length || destination.Length > reader.Length - offset || !reader.TryRead(offset, destination)) throw Corrupt();
    }

    private static InvalidDataException Corrupt() => new("Invalid retained PBT sorted-table framing.");
}
