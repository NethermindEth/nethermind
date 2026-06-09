// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.BTree;

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
    /// <c>false</c> = <c>[Value][FlagByte][LEB128][FullKey]</c> with the pointer at FlagByte
    /// (= MetadataStart);
    /// <c>true</c> = <c>[FlagByte][FullKey][LEB128][Value]</c> with the pointer at FlagByte
    /// (= EntryStart).
    /// </summary>
    /// <remarks>
    /// The dispatch loop reads the 1-byte flag at the current cursor and switches on its
    /// <see cref="BTreeNodeKind"/>: <see cref="BTreeNodeKind.Entry"/> jumps directly to
    /// entry decode; <see cref="BTreeNodeKind.Intermediate"/> loads the node header, does
    /// a floor lookup, and advances the cursor to the matched child's flag byte. Variable
    /// depth is natural — the loop terminates the moment it lands on an Entry-kind flag,
    /// which can happen at any depth (a "direct-entry" child of an intermediate, a child of
    /// a leaf-level intermediate, etc.).
    /// </remarks>
    [SkipLocalsInit]
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
        // Smallest valid HSST: trailer (5 bytes) + root header (12 bytes).
        if (bound.Length < 5 + 12) return false;
        Span<byte> tailBuf = stackalloc byte[5];
        if (!reader.TryRead(bound.Offset + bound.Length - 5, tailBuf)) return false;
        int rootPrefixLen = tailBuf[0];
        int rootSize = tailBuf[1] | (tailBuf[2] << 8);
        int trailerKeyLength = tailBuf[3];
        // tailBuf[4] is IndexType — already consumed by the HsstReader dispatcher.

        // Root prefix bytes seed the root's parentSeparator (non-root nodes get their
        // prefix bytes from the parent's separator during descent; the root has no
        // parent, so the bytes ride the trailer). Size to the actual prefix length
        // (capped at 255 by the trailer's u8 field) rather than a fixed 128 bytes —
        // saves stack frame in the common short-prefix case, and is correct even when
        // the prefix runs to the full 255-byte cap.
        scoped ReadOnlySpan<byte> rootPrefix = default;
        if (rootPrefixLen > 0)
        {
            Span<byte> rootPrefixBuf = stackalloc byte[rootPrefixLen];
            if (!reader.TryRead(bound.Offset + bound.Length - 5 - rootPrefixLen, rootPrefixBuf)) return false;
            rootPrefix = rootPrefixBuf;
        }

        long trailerLen = 5L + rootPrefixLen;
        long rootStart = bound.Offset + bound.Length - trailerLen - rootSize;
        long bufferEnd = bound.Offset + bound.Length - trailerLen;

        return TrySeekFromRoot<TReader, TPin>(in reader, bound, rootStart, bufferEnd,
            rootPrefix, trailerKeyLength, key, exactMatch, keyFirst, out resultBound);
    }

    /// <summary>
    /// Walk-only variant of <see cref="TrySeek"/> for callers that have already resolved the
    /// BTree's root descriptor (start offset, buffer end, root prefix bytes, trailer key length)
    /// — typically because they cache it for the life of their backing container. Skips the
    /// two trailer-region reads that <see cref="TrySeek"/> issues to recover the same values
    /// and jumps straight into the node-walk loop.
    /// </summary>
    /// <remarks>
    /// <paramref name="rootStart"/> is the absolute byte offset of the root node's flag byte
    /// (the same value <see cref="TrySeek"/> computes as
    /// <c>bound.Offset + bound.Length - trailerLen - rootSize</c>). <paramref name="bufferEnd"/>
    /// is the absolute ceiling for speculative node pins (see <see cref="TryLoadNode"/>): it only
    /// bounds how far ahead a node read may pin so it cannot run past the readable region — it is
    /// not a semantic node extent. Nodes self-size from their headers, so a pin window spilling
    /// across following data (hashtable, sibling partitions) is harmless; any value at or before
    /// the reader's length works. The bound is still required because <see cref="DecodeEntry"/>
    /// uses it to derive entry-region offsets and validate value lengths against the HSST's span.
    /// </remarks>
    [SkipLocalsInit]
    public static bool TrySeekFromRoot<TReader, TPin>(
        scoped in TReader reader, Bound bound,
        long rootStart, long bufferEnd,
        scoped ReadOnlySpan<byte> rootPrefix,
        int trailerKeyLength,
        scoped ReadOnlySpan<byte> key,
        bool exactMatch, bool keyFirst, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;

        // Exact-match needs the input key to match the HSST's fixed key length; reject up
        // front before walking the tree. Floor lookups intentionally allow mismatched
        // lengths so callers can seek with a key prefix or sentinel.
        if (exactMatch && key.Length != trailerKeyLength) return false;

        // parentSeparator for the current node — seeded with the trailer's root prefix
        // for the root, then overwritten with each descended-through separator's full
        // bytes (CommonKeyPrefix || storedSlot in lex order). Entries don't have headers,
        // so the value is irrelevant once the cursor reaches one.
        Span<byte> separatorScratch = stackalloc byte[Math.Max(trailerKeyLength, 1)];
        scoped ReadOnlySpan<byte> parentSeparator = rootPrefix;
        long currentAbsStart = rootStart;

        Span<byte> flagBuf = stackalloc byte[1];
        Span<byte> htRec = stackalloc byte[HsstPartitionHashtable.NodeRecordFixedSize];
        Span<byte> htBucket = stackalloc byte[HsstPartitionHashtable.BucketBytes];
        while (true)
        {
            if (!reader.TryRead(currentAbsStart, flagBuf)) return false;
            BTreeNodeKind kind = (BTreeNodeKind)(flagBuf[0] & 0x03);

            if (kind == BTreeNodeKind.Entry)
            {
                return DecodeEntry<TReader, TPin>(in reader, bound, currentAbsStart, key,
                    exactMatch, keyFirst, trailerKeyLength, out resultBound);
            }

            if (kind == BTreeNodeKind.Hashtable)
            {
                // Hashtable node: [Flag][28-byte record][InnerRootPrefix]. On an exact lookup probe
                // one bucket and decode on a hit; on a miss (or a floor) descend into the partition's
                // inner B-tree root the record carries.
                if (!reader.TryRead(currentAbsStart + 1, htRec)) return false;
                long innerRootOffset = ReadU48(htRec);
                long innerBufferEnd = ReadU48(htRec[6..]);
                long hashtableOffset = ReadU48(htRec[12..]);
                long dataRegionStart = ReadU48(htRec[18..]);
                int bucketCount = ReadU24(htRec[24..]);
                int htPrefixLen = htRec[27];

                if (exactMatch && bucketCount > 0)
                {
                    ulong hash = HsstPartitionHashtable.Hash(key);
                    int bucket = HsstPartitionHashtable.BucketIndex(hash, bucketCount);
                    ushort tag = HsstPartitionHashtable.Tag(hash);
                    long bucketAbs = bound.Offset + hashtableOffset + (long)bucket * HsstPartitionHashtable.BucketBytes;
                    if (reader.TryRead(bucketAbs, htBucket))
                    {
                        uint matchMask = HsstPartitionHashtable.MatchMask(htBucket, tag);
                        while (matchMask != 0)
                        {
                            int way = BitOperations.TrailingZeroCount(matchMask);
                            long entryAbs = bound.Offset + dataRegionStart + HsstPartitionHashtable.OffsetAt(htBucket, way);
                            // exactMatch:true — DecodeEntry verifies the full key, so a tag collision
                            // with a different key returns false and we try the next matching way.
                            if (DecodeEntry<TReader, TPin>(in reader, bound, entryAbs, key, exactMatch: true, keyFirst, trailerKeyLength, out resultBound))
                                return true;
                            matchMask &= matchMask - 1;
                        }
                    }
                }

                // Miss or floor: fall through to this partition's inner B-tree from the recorded root.
                if (htPrefixLen > 0)
                {
                    if (!reader.TryRead(currentAbsStart + 1 + HsstPartitionHashtable.NodeRecordFixedSize, separatorScratch[..htPrefixLen])) return false;
                    parentSeparator = separatorScratch[..htPrefixLen];
                }
                else
                {
                    parentSeparator = default;
                }
                currentAbsStart = bound.Offset + innerRootOffset;
                bufferEnd = bound.Offset + innerBufferEnd;
                continue;
            }

            // The flag-byte read above faulted this node's page and warmed its TLB entry, so a prefetch
            // of the node body now lands (instead of being dropped on a TLB miss). Pull the keys the
            // floor-search is about to scan; overlaps with the separator copy below.
            reader.Prefetch(currentAbsStart);

            // Leaf or Intermediate — parse as a BTreeNode node.
            if (!TryLoadNode<TReader, TPin>(in reader, currentAbsStart, bufferEnd, parentSeparator, out BTreeNodeReader node, out TPin pin))
                return false;
            using (pin)
            {
                // FindFloorIndex returns -1 when key < every separator in this node;
                // that means the subtree below has nothing ≤ key and the seek fails.
                int floorIdx = node.FindFloorIndex(key);
                if (floorIdx < 0) return false;

                // Materialize the matched separator's full lex-order bytes so the
                // child (if it's a Leaf/Intermediate) can recover its own prefix bytes
                // from them at the next ReadFromStart call. Cheap to compute even when
                // the child is an Entry — the next iteration will discard parentSeparator
                // before reading the flag byte.
                int sepBytesWritten = node.GetSeparatorBytes(floorIdx, separatorScratch);
                parentSeparator = separatorScratch[..sepBytesWritten];

                ulong childOffset = node.GetUInt64Value(floorIdx);
                currentAbsStart = bound.Offset + (long)childOffset;
            }
        }
    }

    /// <summary>
    /// Decode an entry whose leading flag byte sits at <paramref name="absFlagByteStart"/>.
    /// Splits on <paramref name="keyFirst"/>: <c>true</c> walks forward through
    /// FullKey → LEB128 → Value; <c>false</c> walks forward through LEB128 → FullKey and
    /// derives the value position back-referentially from <c>flagByteStart − valueLength</c>.
    /// </summary>
    [SkipLocalsInit]
    internal static bool DecodeEntry<TReader, TPin>(
        scoped in TReader reader, Bound bound, long absFlagByteStart,
        scoped ReadOnlySpan<byte> key, bool exactMatch, bool keyFirst,
        int trailerKeyLength, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;

        if (keyFirst)
        {
            // [FlagByte][FullKey: trailerKeyLength bytes][LEB128 ValueLength][Value].
            long absKeyStart = absFlagByteStart + 1;
            long absLebStart = absKeyStart + trailerKeyLength;
            long available = bound.Offset + bound.Length - absLebStart;
            if (available <= 0) return false;
            Span<byte> lebBuf = stackalloc byte[10];
            int lebRead = (int)Math.Min(10, available);
            if (!reader.TryRead(absLebStart, lebBuf[..lebRead])) return false;
            int pos = 0;
            long valueLength = Leb128.Read(lebBuf, ref pos);

            if (exactMatch)
            {
                Span<byte> stored = stackalloc byte[trailerKeyLength];
                if (!reader.TryRead(absKeyStart, stored)) return false;
                if (!stored.SequenceEqual(key)) return false;
            }

            resultBound = new Bound(absLebStart + pos, valueLength);
            return true;
        }

        // [Value][FlagByte][LEB128 ValueLength][FullKey]. absFlagByteStart points at the
        // FlagByte (MetadataStart). LEB128 starts at +1; the value sits just before the
        // flag byte and is recovered via ValueStart = MetadataStart − ValueLength.
        long absLebStart_ = absFlagByteStart + 1;
        long available_ = bound.Offset + bound.Length - absLebStart_;
        if (available_ <= 0) return false;
        Span<byte> lebBuf_ = stackalloc byte[10];
        int lebRead_ = (int)Math.Min(10, available_);
        if (!reader.TryRead(absLebStart_, lebBuf_[..lebRead_])) return false;
        int pos_ = 0;
        long valueLength_ = Leb128.Read(lebBuf_, ref pos_);

        if (exactMatch)
        {
            // trailerKeyLength == key.Length was enforced at the top of TrySeek; compare
            // the stored key bytes against the input. Right-sized to the actual key
            // length instead of the legacy 255-byte alloc — saves stack frame and skips
            // zero-init under [SkipLocalsInit].
            Span<byte> stored = stackalloc byte[trailerKeyLength];
            if (!reader.TryRead(absLebStart_ + pos_, stored)) return false;
            if (!stored.SequenceEqual(key)) return false;
        }

        resultBound = new Bound(absFlagByteStart - valueLength_, valueLength_);
        return true;
    }

    /// <summary>
    /// Speculative pin window. Sized to cover a typical small leaf body in one read; nodes
    /// aren't page-aligned so there's no gain from rounding up further. Larger leaves and
    /// intermediates fall back to a precise re-pin.
    /// </summary>
    private const int SpeculativePinSize = 1024;

    /// <summary>
    /// Load the index node whose first byte is at <paramref name="absStart"/> via the reader's
    /// <see cref="IHsstByteReader{TPin}.PinBuffer"/>. On success outs the parsed <see cref="BTreeNodeReader"/>
    /// and the pin (whose <see cref="IBufferPin.Buffer"/> backs <paramref name="node"/>). The
    /// caller must dispose the pin once it's done with the node.
    ///
    /// Issues a single speculative pin sized to <see cref="SpeculativePinSize"/> in the common
    /// case: the header at the front of the window is parsed to compute totalNodeSize, and when
    /// the node fits inside the speculative window we keep that pin instead of re-pinning
    /// precisely. The forward layout means the prefetcher pulls keys/values during the header
    /// read. Cold path (oversized leaves) disposes the speculative pin and re-pins exactly.
    ///
    /// <paramref name="bufferEnd"/> is purely a buffer-availability ceiling: the speculative
    /// window is clamped to <c>bufferEnd - absStart</c> so the pin can never run past the
    /// readable region (<see cref="IHsstByteReader{TPin}.PinBuffer"/> throws on overflow rather
    /// than clamping). It does not delimit the node — the node's size comes from its own header,
    /// so a window that reads into following bytes (a trailing hashtable, the next partition) is
    /// harmless. It need only be ≤ the reader's length and ≥ the node's true end.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryLoadNode<TReader, TPin>(
        scoped in TReader reader, long absStart, long bufferEnd,
        ReadOnlySpan<byte> parentSeparator,
        out BTreeNodeReader node, out TPin pin)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        node = default;
        pin = default;

        long available = bufferEnd - absStart;
        // 12 = fixed header bytes.
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
            // CommonPrefixLen at win[5]; BaseOffset at win[6..12] (not needed for sizing).
            // ValueSize is decoded from the 2-bit ValueSizeCode field in Flags bits 4-5
            // ({2, 3, 4, 6}). KeyType lives in bits 2-3; bits 0-1 carry NodeKind (always
            // Intermediate for nodes parsed here — Entry-kind flag bytes are recognized by
            // the caller before TryLoadNode is invoked).
            int valueSize = ((flags >> 4) & 0b11) switch { 0 => 2, 1 => 3, 2 => 4, _ => 6 };
            int headerSize = 12;
            int keyType = (flags >> 2) & 0x03;
            int keySectionSize = keyType switch { 0 => keySize, _ => keyCount * keySize };
            int valueSectionSize = keyCount * valueSize;
            totalNodeSize = headerSize + keySectionSize + valueSectionSize;

            if (totalNodeSize <= winLen)
            {
                // Hot path: node fits in the speculative window. ReadFromStart parses the
                // header at win[0..] and slices keys/values forward within the node range.
                node = BTreeNodeReader.ReadFromStart(win, 0, parentSeparator);
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
        node = BTreeNodeReader.ReadFromStart(pin.Buffer, 0, parentSeparator);
        return true;
    }

    private static long ReadU48(scoped ReadOnlySpan<byte> src) =>
        src[0]
        | ((long)src[1] << 8)
        | ((long)src[2] << 16)
        | ((long)src[3] << 24)
        | ((long)src[4] << 32)
        | ((long)src[5] << 40);

    private static int ReadU24(scoped ReadOnlySpan<byte> src) =>
        src[0] | (src[1] << 8) | (src[2] << 16);
}
