// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.BTree"/> and
/// <see cref="IndexType.BTreeKeyFirst"/> layouts. Stateless static methods so
/// <see cref="HsstReader{TReader,TPin}"/> can dispatch into them without copying its
/// ref-struct state.
/// </summary>
internal static class HsstBTreeReader
{
    /// <summary>
    /// Exact-match or floor lookup over a BTree HSST. On success sets
    /// <paramref name="resultBound"/> to the value region of the matched entry. Caller
    /// has already read the trailing <see cref="IndexType"/> byte and signals the entry
    /// layout via <paramref name="keyFirst"/>:
    /// <c>false</c> = <c>[Value][LEB128][FullKey]</c> with pointer at LEB128;
    /// <c>true</c> = <c>[FullKey][LEB128][Value]</c> with pointer at FullKey byte 0.
    /// </summary>
    public static bool TrySeek<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key,
        bool exactMatch, bool keyFirst, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;

        // Trailer: [RootPrefix bytes][RootPrefixLen u8][RootSize u16 LE][KeyLength u8][IndexType u8].
        // Read the fixed 5-byte tail first to learn RootPrefixLen / RootSize / KeyLength;
        // the prefix bytes (if any) sit immediately before that.
        if (bound.Length < 5 + 12) return false;
        Span<byte> tailBuf = stackalloc byte[5];
        if (!reader.TryRead(bound.Offset + bound.Length - 5, tailBuf)) return false;
        int rootPrefixLen = tailBuf[0];
        int rootSize = tailBuf[1] | (tailBuf[2] << 8);
        int trailerKeyLength = tailBuf[3];
        // tailBuf[4] is IndexType — already consumed by the HsstReader dispatcher.

        // Exact-match needs the input key to match the HSST's fixed key length; reject up
        // front before walking the tree. Floor lookups intentionally allow mismatched
        // lengths so callers can seek with a key prefix or sentinel.
        if (exactMatch && key.Length != trailerKeyLength) return false;

        // Root prefix bytes seed the root's parentSeparator (non-root nodes get their
        // prefix bytes from the parent's separator during descent; the root has no
        // parent, so the bytes ride the trailer).
        Span<byte> rootPrefixBuf = stackalloc byte[128];
        scoped ReadOnlySpan<byte> rootPrefix = default;
        if (rootPrefixLen > 0)
        {
            if (!reader.TryRead(bound.Offset + bound.Length - 5 - rootPrefixLen, rootPrefixBuf[..rootPrefixLen])) return false;
            rootPrefix = rootPrefixBuf[..rootPrefixLen];
        }

        long trailerLen = 5L + rootPrefixLen;
        long currentAbsStart = bound.Offset + bound.Length - trailerLen - rootSize;
        long scopeEnd = bound.Offset + bound.Length - trailerLen;

        // parentSeparator for the current node — seeded with the trailer's root prefix
        // for the root, then overwritten with each descended-through separator's full
        // bytes (CommonKeyPrefix || storedSlot in lex order).
        Span<byte> separatorScratch = stackalloc byte[Math.Max(trailerKeyLength, 1)];
        scoped ReadOnlySpan<byte> parentSeparator = rootPrefix;

        while (true)
        {
            if (!TryLoadNode<TReader, TPin>(in reader, currentAbsStart, scopeEnd, parentSeparator, out HsstIndex node, out TPin pin))
                return false;
            using (pin)
            {
                if (node.IsIntermediate)
                {
                    // Phantom slot 0 restored: every child has a separator in this node.
                    // FindFloorIndex returns the matched child index; "no floor" means
                    // the search key falls before children[0]'s separator, so the
                    // subtree contains nothing ≤ key and the seek fails.
                    int floorIdx = node.FindFloorIndex(key);
                    if (floorIdx < 0) return false;

                    // Materialize the matched separator's full lex-order bytes so the
                    // child can recover its own prefix bytes from them at the next
                    // ReadFromStart call.
                    int sepBytesWritten = node.GetSeparatorBytes(floorIdx, separatorScratch);
                    parentSeparator = separatorScratch[..sepBytesWritten];

                    ulong childOffset = node.GetUInt64Value(floorIdx);
                    currentAbsStart = bound.Offset + (long)childOffset;
                    continue;
                }

                if (!node.TryGetFloor(key, out ReadOnlySpan<byte> separator, out ReadOnlySpan<byte> metaBytes))
                    return false;

                // Cheap reject path: the stored full key starts with (commonPrefix + separator),
                // so the input must too. Saves a length-mismatch read in the common
                // exact-miss case. Skip when the leaf stores keys in LE byte order — the
                // `separator` bytes are byte-reversed, so a direct StartsWith comparison would
                // be incorrect, and the storage-read SequenceEqual below still catches mismatches.
                if (exactMatch && !node.Metadata.IsKeyLittleEndian)
                {
                    ReadOnlySpan<byte> p = node.CommonKeyPrefix;
                    if (!key.StartsWith(p) || !key[p.Length..].StartsWith(separator)) return false;
                }

                long entryRel = (long)(BSearchIndex.BSearchIndexReader.ReadUInt64LE(metaBytes) + node.Metadata.BaseOffset);
                long absEntryStart = bound.Offset + entryRel;

                if (keyFirst)
                {
                    // Entry: [FullKey: trailerKeyLength bytes][LEB128 ValueLength][Value].
                    // absEntryStart points at FullKey byte 0.
                    long absLebStart = absEntryStart + trailerKeyLength;
                    long available = bound.Offset + bound.Length - absLebStart;
                    if (available <= 0) return false;
                    Span<byte> lebBuf = stackalloc byte[10];
                    int lebRead = (int)Math.Min(10, available);
                    if (!reader.TryRead(absLebStart, lebBuf[..lebRead])) return false;
                    int pos = 0;
                    long valueLength = Leb128.Read(lebBuf, ref pos);

                    if (exactMatch)
                    {
                        Span<byte> stored = stackalloc byte[255];
                        Span<byte> storedSlice = stored[..trailerKeyLength];
                        if (!reader.TryRead(absEntryStart, storedSlice)) return false;
                        if (!storedSlice.SequenceEqual(key)) return false;
                    }

                    resultBound = new Bound(absLebStart + pos, valueLength);
                    return true;
                }
                else
                {
                    // Entry: [Value][LEB128 ValueLength][FullKey]. absEntryStart points at
                    // the LEB128 byte (MetadataStart). Read up to 10 bytes for the LEB128
                    // (max 10 bytes for a 64-bit varint). The key length comes from the
                    // trailer, not from per-entry storage.
                    long available = bound.Offset + bound.Length - absEntryStart;
                    if (available <= 0) return false;
                    Span<byte> lebBuf = stackalloc byte[10];
                    int lebRead = (int)Math.Min(10, available);
                    if (!reader.TryRead(absEntryStart, lebBuf[..lebRead])) return false;

                    int pos = 0;
                    long valueLength = Leb128.Read(lebBuf, ref pos);

                    if (exactMatch)
                    {
                        // trailerKeyLength == key.Length was already enforced at the top of
                        // TrySeek; compare the stored key bytes against the input. Stored
                        // key fits in 255 bytes — single read + compare, no chunking.
                        Span<byte> stored = stackalloc byte[255];
                        Span<byte> storedSlice = stored[..trailerKeyLength];
                        if (!reader.TryRead(absEntryStart + pos, storedSlice)) return false;
                        if (!storedSlice.SequenceEqual(key)) return false;
                    }

                    // value bytes are immediately before the metaStart
                    resultBound = new Bound(absEntryStart - valueLength, valueLength);
                    return true;
                }
            }
        }
    }

    /// <summary>
    /// Speculative pin window. Sized to cover a typical small leaf body in one read; nodes
    /// aren't page-aligned so there's no gain from rounding up further. Larger leaves and
    /// intermediates fall back to a precise re-pin.
    /// </summary>
    private const int SpeculativePinSize = 1024;

    /// <summary>
    /// Load the index node whose first byte is at <paramref name="absStart"/> via the reader's
    /// <see cref="IHsstByteReader{TPin}.PinBuffer"/>. On success outs the parsed <see cref="HsstIndex"/>
    /// and the pin (whose <see cref="IBufferPin.Buffer"/> backs <paramref name="node"/>). The
    /// caller must dispose the pin once it's done with the node.
    ///
    /// Issues a single speculative pin sized to <see cref="SpeculativePinSize"/> in the common
    /// case: the header at the front of the window is parsed to compute totalNodeSize, and when
    /// the node fits inside the speculative window we keep that pin instead of re-pinning
    /// precisely. The forward layout means the prefetcher pulls keys/values during the header
    /// read. Cold path (oversized leaves) disposes the speculative pin and re-pins exactly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryLoadNode<TReader, TPin>(
        scoped in TReader reader, long absStart, long scopeEnd,
        ReadOnlySpan<byte> parentSeparator,
        out HsstIndex node, out TPin pin)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        node = default;
        pin = default;

        long available = scopeEnd - absStart;
        if (available < 12) return false;

        int winLen = (int)Math.Min(SpeculativePinSize, available);

        TPin speculativePin = reader.PinBuffer(absStart, winLen);
        bool keepSpeculative = false;
        int totalNodeSize;
        try
        {
            ReadOnlySpan<byte> win = speculativePin.Buffer;
            byte flags = win[0];
            int keyCount = BinaryPrimitives.ReadUInt16LittleEndian(win[1..]);
            int keySize = BinaryPrimitives.ReadUInt16LittleEndian(win[3..]);
            int valueSize = win[5];
            // BaseOffset (6 bytes) at win[6..12]; we don't need it here, just the size.
            int headerSize = 12;
            if ((flags & 0x40) != 0)
            {
                if (winLen < 13) goto Cold;
                // CommonPrefixLen byte sits at win[12]; the prefix bytes themselves are
                // out-of-band (delivered via parentSeparator) unless bit 7 marks them
                // inline (legacy-style root encoding — HSST callers no longer set bit 7
                // since the root prefix rides the trailer, but the reader handles both).
                int prefixLen = win[12];
                headerSize += 1;
                if ((flags & 0x80) != 0) headerSize += prefixLen;
            }
            int keyType = (flags >> 1) & 0x03;
            int keySectionSize = keyType switch { 0 => keySize, _ => keyCount * keySize };
            // Values are always Uniform — bits 3-4 of flags are reserved/zero.
            int valueSectionSize = keyCount * valueSize;
            totalNodeSize = headerSize + keySectionSize + valueSectionSize;

            if (totalNodeSize <= winLen)
            {
                // Hot path: node fits in the speculative window. ReadFromStart parses the
                // header at win[0..] and slices keys/values forward within the node range.
                node = HsstIndex.ReadFromStart(win, 0, parentSeparator);
                pin = speculativePin;
                keepSpeculative = true;
                return true;
            }
        }
        finally
        {
            if (!keepSpeculative) speculativePin.Dispose();
        }

        // Cold path: node larger than the speculative window. Pin precisely.
        pin = reader.PinBuffer(absStart, totalNodeSize);
        node = HsstIndex.ReadFromStart(pin.Buffer, 0, parentSeparator);
        return true;

    Cold:
        // Window too small to even read the common-prefix length byte. The HasCommonKeyPrefix
        // bit is set yet available < 13, which is structurally impossible for a well-formed
        // HSST — bail rather than risk an out-of-bounds read.
        return false;
    }
}
