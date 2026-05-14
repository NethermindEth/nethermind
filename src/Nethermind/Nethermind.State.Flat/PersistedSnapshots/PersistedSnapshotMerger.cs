// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.Storage;
using HsstEnumerator = Nethermind.State.Flat.Hsst.HsstEnumerator<Nethermind.State.Flat.Storage.WholeReadSessionReader, Nethermind.State.Flat.Hsst.NoOpPin>;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// N-way merge implementation for persisted snapshots. Driven by
/// <see cref="PersistedSnapshotCompactor"/> during logarithmic compaction: takes
/// N oldest-first persisted snapshots and emits a single columnar HSST byte
/// stream into the caller's writer. All inputs are blob-backed (trie-node RLP
/// values are <see cref="NodeRef"/>s pointing into blob arenas), so the merge
/// walks column-by-column without any Full→Linked pre-conversion.
/// </summary>
public static class PersistedSnapshotMerger
{
    private const int StorageHashPrefixLength = 20;

    // Per-address DenseByteIndex max tag + 1 (sub-tags 0x01..0x06 are populated). Allows
    // a single TryResolveAll per source to retrieve every sub-tag bound at once.
    private const int PerAddrSubTagCount = 7;

    // Cached raw view fields for an open WholeReadSession. Used by the N-way merge helpers
    // to amortise the per-call ObjectDisposedException check + interface-dispatch cost of
    // WholeReadSession.GetReader over the entire merge loop. Callers populate one entry per
    // source at merge setup; the underlying session must outlive every call to Reader.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static WholeReadSessionReader Reader((IntPtr Ptr, long Len) v)
    {
        unsafe { return new WholeReadSessionReader((byte*)v.Ptr, v.Len); }
    }

    /// <summary>
    /// N-way merge of N persisted snapshots (oldest-first) into output buffer.
    /// Pre-converts all Full snapshots to Linked so the merge only handles Linked snapshots
    /// (all trie values are already NodeRefs). This eliminates the dual code path in trie merges.
    /// </summary>
    internal static void NWayMergeSnapshots<TWriter, TReader, TPin>(PersistedSnapshotList snapshots, ref TWriter writer, SortedSet<ushort> referencedBlobArenaIds, BloomFilter? bloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // Open one WholeReadSession per source for the whole merge — every column helper
        // reads through these without re-opening per-helper sessions (which would mmap +
        // MADV_NORMAL on open and MADV_DONTNEED on close between columns, dropping pages
        // we'd then re-fault for the next column). One open per source, one close at the
        // end, regardless of how many columns we walk.
        int n = snapshots.Count;
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        using NativeMemoryList<(IntPtr Ptr, long Len)> viewsList = new(n, n);
        WholeReadSession[] sessions = sessionsList.UnsafeGetInternalArray();
        Span<(IntPtr Ptr, long Len)> views = viewsList.AsSpan();
        try
        {
            for (int i = 0; i < n; i++)
            {
                sessions[i] = snapshots[i].BeginWholeReadSession();
                views[i] = sessions[i].GetRawView();
            }

            NWayMergeSnapshotsWithViews<TWriter, TReader, TPin>(views, ref writer, referencedBlobArenaIds, bloom);
        }
        finally
        {
            for (int i = 0; i < n; i++) sessions[i]?.Dispose();
        }
    }

    /// <summary>
    /// Variant of <see cref="NWayMergeSnapshots"/> that takes pre-opened mmap views instead
    /// of opening (and closing) one <see cref="WholeReadSession"/> per source. Used by the
    /// compactor, which opens the sessions once at the top of <c>CompactRange</c> so the
    /// ref-ids read and the merge share the same mmap views.
    /// </summary>
    internal static void NWayMergeSnapshotsWithViews<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, ref TWriter writer,
        SortedSet<ushort> referencedBlobArenaIds, BloomFilter? bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // All snapshots are blob-backed (values in trie columns are NodeRefs), so we can
        // merge them directly without any Full→Linked pre-conversion stage. Columns are
        // emitted in the on-disk order the DenseByteIndex outer expects: metadata (0x00),
        // account (0x01), state-node (0x03), state-top-nodes (0x05), state-fallback (0x06).
        // Storage-trie data rides along inside the per-address column 0x01 as sub-tags, so
        // 0x07/0x08 are gone from the layout.
        using HsstDenseByteIndexBuilder<TWriter> outerBuilder = new(ref writer);

        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayMetadataMerge<TWriter, TReader, TPin>(views, ref valueWriter, referencedBlobArenaIds);
            outerBuilder.FinishValueWrite(PersistedSnapshot.MetadataTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayMergeAccountColumn<TWriter, TReader, TPin>(views, PersistedSnapshot.AccountColumnTag, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshot.AccountColumnTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayStreamingMerge<TWriter, TReader, TPin>(views, PersistedSnapshot.StateNodeTag, ref valueWriter, keySize: 8);
            outerBuilder.FinishValueWrite(PersistedSnapshot.StateNodeTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayStreamingMerge<TWriter, TReader, TPin>(views, PersistedSnapshot.StateTopNodesTag, ref valueWriter, keySize: 4);
            outerBuilder.FinishValueWrite(PersistedSnapshot.StateTopNodesTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayStreamingMerge<TWriter, TReader, TPin>(views, PersistedSnapshot.StateNodeFallbackTag, ref valueWriter, keySize: 33);
            outerBuilder.FinishValueWrite(PersistedSnapshot.StateNodeFallbackTag);
        }

        outerBuilder.Build();
    }

    // --- N-Way merge methods ---

    /// <summary>
    /// N-way streaming merge of a column across N snapshots. On key collision, newest (highest index) wins.
    /// Uses <see cref="HsstEnumerator"/> for zero-allocation cursor-based enumeration.
    /// The caller supplies a parallel <paramref name="views"/> span — one entry per source —
    /// so the helper does not re-open per-reservation mmap views inside its scope.
    /// </summary>
    private static void NWayStreamingMerge<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, byte[] tag, ref TWriter writer,
        int keySize) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        using ArrayPoolList<HsstEnumerator> enums = new(n, n);
        using NativeMemoryList<bool> hasMore = new(n, n);
        // Cache each source's current logical key once per MoveNext so the O(log N) cursor
        // and O(N) match-detection scans don't redo CopyCurrentLogicalKey per output key.
        int keyStride = Math.Max(1, keySize);
        using NativeMemoryList<byte> keyBufList = new(n * keyStride, n * keyStride);
        Span<byte> keyBuf = keyBufList.AsSpan();

        try
        {
            for (int i = 0; i < n; i++)
            {
                WholeReadSessionReader r = Reader(views[i]);
                HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
                (long Offset, long Length) cb = hsst.TrySeek(tag, out Bound cbOut) ? (cbOut.Offset, cbOut.Length) : (0, 0);
                enums[i] = new HsstEnumerator(in r, new Bound(cb.Offset, cb.Length));
                hasMore[i] = enums[i].MoveNext(in r);
                if (hasMore[i])
                    enums[i].CopyCurrentLogicalKey(in r, keyBuf.Slice(i * keyStride, keySize));
            }

            int pow2N = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, n));
            Span<int> srcMap = stackalloc int[Math.Max(1, n)];
            for (int i = 0; i < n; i++) srcMap[i] = i;
            Span<int> matchingBuf = stackalloc int[Math.Max(1, n)];
            Span<int> tree = stackalloc int[2 * pow2N];

            NWayMergeCursor cursor = new(
                enums.UnsafeGetInternalArray(), hasMore.AsSpan(),
                views, srcMap, n, keySize, keyStride, keyBuf, matchingBuf, tree);

            using HsstPackedArrayBuilder<TWriter> builder = new(ref writer, keySize, NodeRef.Size);

            while (cursor.MoveNext())
            {
                int minIdx = cursor.MinIdx;
                Bound valBound = enums[minIdx].CurrentValue;
                WholeReadSessionReader minIdxReader = Reader(views[minIdx]);
                using NoOpPin valPin = minIdxReader.PinBuffer(valBound.Offset, valBound.Length);
                builder.Add(cursor.MinKey, valPin.Buffer);

                cursor.AdvanceMatching();
            }

            builder.Build();
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i].Dispose();
        }
    }
    /// <summary>
    /// N-way merge of the account column (tag 0x01) across N snapshots.
    /// Outer: 20-byte address keys (minSep=4). Addresses with a single matching source
    /// byte-copy the per-address HSST blob verbatim (every internal pointer is
    /// HSST-relative, so a relocation stays readable); collisions go through
    /// <see cref="NWayMergePerAddressHsst"/>.
    /// </summary>
    private static void NWayMergeAccountColumn<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, byte[] tag, ref TWriter writer, BloomFilter? bloom = null) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        using ArrayPoolList<HsstEnumerator> enumsList = new(n, n);
        using NativeMemoryList<bool> hasMoreList = new(n, n);
        HsstEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        Span<bool> hasMore = hasMoreList.AsSpan();

        // Cache each source's current 20-byte address-hash key (stride 32 with room).
        const int KeyStride = 32;
        const int AddrKeyLen = StorageHashPrefixLength;
        Span<byte> keyBuf = stackalloc byte[n * KeyStride];

        // Reusable work buffers for the per-address slot prefix/suffix HSST builders.
        // Declared at column scope so the rentals stay alive across every merged
        // address — the prefix builder is created once per address and the suffix
        // builder once per prefix group per address, so churn dominates otherwise.
        // Plain locals (not `using`) so they can be passed by ref through the call
        // chain into the builder constructors.
        HsstBTreeBuilderBuffers slotPrefixBuffers = new();
        HsstBTreeBuilderBuffers slotSuffixBuffers = new();

        try
        {
            for (int i = 0; i < n; i++)
            {
                WholeReadSessionReader r = Reader(views[i]);
                HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
                (long Offset, long Length) cb = hsst.TrySeek(tag, out Bound cbOut) ? (cbOut.Offset, cbOut.Length) : (0, 0);
                enums[i] = new HsstEnumerator(in r, new Bound(cb.Offset, cb.Length));
                hasMore[i] = enums[i].MoveNext(in r);
                if (hasMore[i])
                    enums[i].CopyCurrentLogicalKey(in r, keyBuf.Slice(i * KeyStride, AddrKeyLen));
            }

            int pow2N = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, n));
            Span<int> srcMap = stackalloc int[Math.Max(1, n)];
            for (int i = 0; i < n; i++) srcMap[i] = i;
            Span<int> matchingBuf = stackalloc int[Math.Max(1, n)];
            Span<int> tree = stackalloc int[2 * pow2N];

            NWayMergeCursor cursor = new(
                enums, hasMore, views, srcMap, n, AddrKeyLen, KeyStride, keyBuf, matchingBuf, tree);

            using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, StorageHashPrefixLength, new HsstBTreeOptions { MinSeparatorLength = 4 });

            while (cursor.MoveNext())
            {
                ReadOnlySpan<byte> minKey = cursor.MinKey;
                int matchCount = cursor.MatchCount;
                ReadOnlySpan<int> matchingSources = cursor.MatchingSources;

                if (matchCount == 1)
                {
                    // Single-source fast path: byte-copy the source's per-address HSST blob.
                    // HSST internal pointers are HSST-relative (childOffset / dense-index ends
                    // are stored as deltas from the blob start), so a verbatim relocation to
                    // the destination writer position stays readable. The per-address sub-tags
                    // (account 0x05, self-destruct 0x06, slots 0x04, storage 0x01/0x02/0x03)
                    // ride along inside the copied blob — no per-sub-tag merge needed. Streamed
                    // via the long-aware IByteBufferWriter.Copy so blobs over the 2 GiB single-
                    // Span ceiling stay safe.
                    int srcIdx = matchingSources[0];
                    Bound vb = enums[srcIdx].CurrentValue;
                    WholeReadSessionReader srcReader = Reader(views[srcIdx]);
                    ref TWriter perAddrWriter = ref builder.BeginValueWrite();
                    IByteBufferWriter.Copy<TWriter, WholeReadSessionReader, NoOpPin>(ref perAddrWriter, in srcReader, vb);
                    builder.FinishValueWrite(minKey);
                    if (bloom is not null)
                    {
                        ulong addrKey = MemoryMarshal.Read<ulong>(minKey);
                        bloom.Add(addrKey);
                        HsstReader<WholeReadSessionReader, NoOpPin> slot = new(in srcReader, vb);
                        if (slot.TrySeek(PersistedSnapshot.SlotSubTag, out Bound slotBound))
                            AddSlotKeysToBloom<WholeReadSessionReader, NoOpPin>(in srcReader, slotBound, addrKey, bloom);
                    }
                }
                else
                {
                    // M > 1 sources collide on this address: merge per-address HSSTs.
                    ref TWriter perAddrWriter = ref builder.BeginValueWrite();
                    ulong addrKey = 0;
                    if (bloom is not null)
                    {
                        addrKey = MemoryMarshal.Read<ulong>(minKey);
                        bloom.Add(addrKey);
                    }
                    NWayMergePerAddressHsst<TWriter, TReader, TPin>(
                        enums, matchingSources, matchCount, views,
                        ref perAddrWriter, ref slotPrefixBuffers, ref slotSuffixBuffers,
                        bloom, addrKey);
                    builder.FinishValueWrite(minKey);
                }

                cursor.AdvanceMatching();
            }

            builder.Build();
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i].Dispose();
            slotSuffixBuffers.Dispose();
            slotPrefixBuffers.Dispose();
        }
    }

    /// <summary>
    /// N-way merge of per-address HSSTs from M sources (oldest-first by matchingSources order).
    /// Sub-tags emitted in ascending byte order so the DenseByteIndex builder accepts them:
    /// - 0x01 StorageTop: streaming merge of inner (3-byte path → NodeRef) PackedArrays.
    ///   No destruct barrier — orphan nodes are unreachable from the new storage root.
    /// - 0x02 StorageCompact: same as 0x01 with 8-byte path keys.
    /// - 0x03 StorageFallback: same as 0x01 with 33-byte path keys.
    /// - 0x04 Slots: find newest destruct barrier, merge slots from barrier..M-1 via nested streaming merge
    /// - 0x05 Account: newest wins (walk M-1..0, first with AccountSubTag)
    /// - 0x06 SelfDestruct: iterate 0..M-1, apply TryAdd semantics
    /// </summary>
    private static void NWayMergePerAddressHsst<TWriter, TReader, TPin>(
        HsstEnumerator[] outerEnums, scoped ReadOnlySpan<int> matchingSources, int matchCount,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ref TWriter writer,
        scoped ref HsstBTreeBuilderBuffers slotPrefixBuffers,
        scoped ref HsstBTreeBuilderBuffers slotSuffixBuffers,
        BloomFilter? bloom = null, ulong addrBloomKey = 0) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // Get per-address HSST bounds (absolute offset from snapshot start) for each matching source.
        using NativeMemoryList<(long Offset, long Length)> perAddrBoundsList = new(matchCount, matchCount);
        Span<(long Offset, long Length)> perAddrBounds = perAddrBoundsList.AsSpan();
        for (int j = 0; j < matchCount; j++)
        {
            int srcIdx = matchingSources[j];
            // CurrentValue.Offset is snapshot-absolute (the enumerator was scoped to the column
            // within the whole snapshot), so it can be stored directly.
            Bound vb = outerEnums[srcIdx].CurrentValue;
            perAddrBounds[j] = (vb.Offset, vb.Length);
        }

        // Resolve every sub-tag bound for every matching source in a single pass through
        // each source's DenseByteIndex. Replaces 6+ per-source TrySeek calls (each of which
        // re-read the trailer and re-pinned the ends array). Indexed as
        // subTagBounds[j * PerAddrSubTagCount + tag] for source j, sub-tag value `tag`.
        using NativeMemoryList<Bound> subTagBoundsList = new(matchCount * PerAddrSubTagCount, matchCount * PerAddrSubTagCount);
        Span<Bound> subTagBounds = subTagBoundsList.AsSpan();
        for (int j = 0; j < matchCount; j++)
        {
            WholeReadSessionReader r = Reader(views[matchingSources[j]]);
            HsstDenseByteIndexReader.TryResolveAll<WholeReadSessionReader, NoOpPin>(
                in r,
                new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length),
                subTagBounds.Slice(j * PerAddrSubTagCount, PerAddrSubTagCount));
        }

        // perAddrBuilder is passed to several helpers by ref, so it can't be a `using`
        // declaration (the compiler refuses ref to using-variables). Manage its disposal
        // with a try/finally instead.
        HsstDenseByteIndexBuilder<TWriter> perAddrBuilder = new(ref writer);
        try
        {

            // Sub-tags 0x01 / 0x02 / 0x03: storage trie top / compact / fallback. Each source
            // carries an inner HSST keyed by encoded TreePath; values are NodeRefs (since
            // NWayMerge converts Full→Linked first). N-way streaming merge per sub-tag with
            // newest-wins on key collision; no destruct barrier since orphan nodes are
            // unreachable from the new storage root.
            MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, views, subTagBounds,
                ref perAddrBuilder, PersistedSnapshot.StorageTopSubTag, subTagIdx: PersistedSnapshot.StorageTopSubTag[0], innerKeySize: 4);
            MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, views, subTagBounds,
                ref perAddrBuilder, PersistedSnapshot.StorageCompactSubTag, subTagIdx: PersistedSnapshot.StorageCompactSubTag[0], innerKeySize: 8);
            MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, views, subTagBounds,
                ref perAddrBuilder, PersistedSnapshot.StorageFallbackSubTag, subTagIdx: PersistedSnapshot.StorageFallbackSubTag[0], innerKeySize: 33);

            // Find newest destruct barrier: newest j where SelfDestructSubTag is present and
            // marks "destructed" ([0x00]). With DenseByteIndex per-address encoding, sub-tag
            // values are presence-marked: length 0 = absent, [0x00] = destructed, [0x01] = new.
            int sdTag = PersistedSnapshot.SelfDestructSubTag[0];
            int destructBarrier = -1;
            for (int j = 0; j < matchCount; j++)
            {
                Bound sdb = subTagBounds[j * PerAddrSubTagCount + sdTag];
                if (sdb.Length != 1) continue;
                WholeReadSessionReader r = Reader(views[matchingSources[j]]);
                using NoOpPin sdPin = r.PinBuffer(sdb.Offset, 1);
                if (sdPin.Buffer[0] == 0x00)
                    destructBarrier = j;
            }

            // Sub-tag 0x04: Slots
            // Merge slots only from max(0, destructBarrier)..matchCount-1. The slot merge
            // emits bloom adds inline from the merged stream (one walk per source) — the
            // separate pre-pass that did a duplicate walk per source has been removed.
            int slotStart = Math.Max(0, destructBarrier);
            int slotTag = PersistedSnapshot.SlotSubTag[0];

            {
                int slotSourceCount = 0;
                int slotCapacity = matchCount - slotStart;
                using NativeMemoryList<int> slotSourcesList = new(slotCapacity, slotCapacity);
                using NativeMemoryList<(long Offset, long Length)> slotBoundsList = new(slotCapacity, slotCapacity);
                Span<int> slotSources = slotSourcesList.AsSpan();
                Span<(long Offset, long Length)> slotBounds = slotBoundsList.AsSpan();
                for (int j = slotStart; j < matchCount; j++)
                {
                    Bound slotBound = subTagBounds[j * PerAddrSubTagCount + slotTag];
                    if (slotBound.Length > 0)
                    {
                        slotSources[slotSourceCount] = matchingSources[j];
                        slotBounds[slotSourceCount] = (slotBound.Offset, slotBound.Length);
                        slotSourceCount++;
                    }
                }

                if (slotSourceCount == 1)
                {
                    // Single-source fast path: byte-copy the source's slot HSST blob.
                    // HSST internal pointers are HSST-relative, so the relocated blob stays
                    // readable. Streamed via the long-aware IByteBufferWriter.Copy so a slot
                    // HSST above the 2 GiB single-Span ceiling stays safe. Bloom adds are
                    // walked separately since this path skips NWayInnerSlotMerge.
                    WholeReadSessionReader slotReader = Reader(views[slotSources[0]]);
                    Bound slotBlob = new(slotBounds[0].Offset, slotBounds[0].Length);
                    ref TWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                    IByteBufferWriter.Copy<TWriter, WholeReadSessionReader, NoOpPin>(ref slotWriter, in slotReader, slotBlob);
                    perAddrBuilder.FinishValueWrite(PersistedSnapshot.SlotSubTag);
                    if (bloom is not null)
                        AddSlotKeysToBloom<WholeReadSessionReader, NoOpPin>(in slotReader, slotBlob, addrBloomKey, bloom);
                }
                else if (slotSourceCount > 1)
                {
                    // M > 1 sources collide on this address's slots: streaming merge through
                    // NWayNestedStreamingSlotMerge / NWayInnerSlotMerge folds bloom adds in.
                    using ArrayPoolList<HsstEnumerator> slotEnumsList = new(slotSourceCount, slotSourceCount);
                    using NativeMemoryList<bool> slotHasMoreList = new(slotSourceCount, slotSourceCount);
                    using NativeMemoryList<(IntPtr Ptr, long Len)> slotViewsList = new(slotSourceCount, slotSourceCount);
                    HsstEnumerator[] slotEnums = slotEnumsList.UnsafeGetInternalArray();
                    Span<bool> slotHasMore = slotHasMoreList.AsSpan();
                    Span<(IntPtr Ptr, long Len)> slotViews = slotViewsList.AsSpan();
                    try
                    {
                        for (int j = 0; j < slotSourceCount; j++)
                        {
                            slotViews[j] = views[slotSources[j]];
                            WholeReadSessionReader slotReader = Reader(slotViews[j]);
                            slotEnums[j] = new HsstEnumerator(in slotReader, new Bound(slotBounds[j].Offset, slotBounds[j].Length));
                            slotHasMore[j] = slotEnums[j].MoveNext(in slotReader);
                        }

                        ref TWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                        NWayNestedStreamingSlotMerge<TWriter, TReader, TPin>(
                            slotEnums, slotHasMore, slotSourceCount, slotViews,
                            ref slotWriter,
                            ref slotPrefixBuffers, ref slotSuffixBuffers,
                            bloom, addrBloomKey);
                        perAddrBuilder.FinishValueWrite(PersistedSnapshot.SlotSubTag);
                    }
                    finally
                    {
                        for (int j = 0; j < slotSourceCount; j++) slotEnums[j].Dispose();
                    }
                }
            }

            // Sub-tag 0x05: Account — newest wins (walk M-1..0, first present (length>0)).
            {
                int acctTag = PersistedSnapshot.AccountSubTag[0];
                for (int j = matchCount - 1; j >= 0; j--)
                {
                    Bound ab = subTagBounds[j * PerAddrSubTagCount + acctTag];
                    if (ab.Length == 0) continue;
                    WholeReadSessionReader r = Reader(views[matchingSources[j]]);
                    using NoOpPin acctPin = r.PinBuffer(ab.Offset, ab.Length);
                    perAddrBuilder.Add(PersistedSnapshot.AccountSubTag, acctPin.Buffer);
                    break;
                }
            }

            // Sub-tag 0x06: SelfDestruct — iterate 0..M-1, apply TryAdd semantics. Presence
            // is signalled by length>0 ([0x00]=destructed, [0x01]=new); absent entries (gap-
            // filled length 0 under DenseByteIndex) are ignored. Track the winning bound
            // snapshot-absolute so we can re-pin at the end without holding a span across
            // iterations.
            {
                int sdSrcJ = -1;
                long sdValOff = 0;
                long sdValLen = 0;

                for (int j = 0; j < matchCount; j++)
                {
                    Bound sdb = subTagBounds[j * PerAddrSubTagCount + sdTag];
                    if (sdb.Length == 0) continue;

                    if (sdSrcJ < 0)
                    {
                        sdSrcJ = j;
                        sdValOff = sdb.Offset;
                        sdValLen = sdb.Length;
                    }
                    else
                    {
                        // TryAdd: newer=destructed ([0x00]) -> destructed wins; newer=new ([0x01]) -> keep older.
                        WholeReadSessionReader r = Reader(views[matchingSources[j]]);
                        using NoOpPin firstBytePin = r.PinBuffer(sdb.Offset, 1);
                        if (firstBytePin.Buffer[0] == 0x00)
                        {
                            sdSrcJ = j;
                            sdValOff = sdb.Offset;
                            sdValLen = sdb.Length;
                        }
                    }
                }

                if (sdSrcJ >= 0)
                {
                    WholeReadSessionReader r = Reader(views[matchingSources[sdSrcJ]]);
                    using NoOpPin sdPin = r.PinBuffer(sdValOff, sdValLen);
                    perAddrBuilder.Add(PersistedSnapshot.SelfDestructSubTag, sdPin.Buffer);
                }
            }

            perAddrBuilder.Build();
        }
        finally
        {
            perAddrBuilder.Dispose();
        }
    }

    /// <summary>
    /// Merge a single storage-trie sub-tag (0x01 top, 0x02 compact, or 0x03 fallback) across the M
    /// matching per-address sources into <paramref name="perAddrBuilder"/>. Each source's
    /// sub-tag value is an inner HSST(BTree) keyed by encoded TreePath; values are
    /// NodeRefs (NWayMergeSnapshots converts every Full input to Linked first). When
    /// only one source has the sub-tag, copies its bytes verbatim. With multiple sources,
    /// runs an N-way streaming merge into a fixed-size <see cref="HsstPackedArrayBuilder{TWriter}"/>
    /// (innerKeySize → NodeRef.Size). Newest wins on key collision; storage trie nodes
    /// are content-addressable so duplicate keys carry identical NodeRefs in practice.
    /// </summary>
    private static void MergeStorageTrieSubTag<TWriter, TReader, TPin>(
        scoped ReadOnlySpan<int> matchingSources, int matchCount,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ReadOnlySpan<Bound> subTagBounds,
        ref HsstDenseByteIndexBuilder<TWriter> perAddrBuilder,
        byte[] subTag,
        int subTagIdx,
        int innerKeySize) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using NativeMemoryList<int> srcsList = new(matchCount, matchCount);
        using NativeMemoryList<(long Offset, long Length)> boundsList = new(matchCount, matchCount);
        Span<int> srcs = srcsList.AsSpan();
        Span<(long Offset, long Length)> subBounds = boundsList.AsSpan();

        int active = 0;
        for (int j = 0; j < matchCount; j++)
        {
            Bound sb = subTagBounds[j * PerAddrSubTagCount + subTagIdx];
            if (sb.Length > 0)
            {
                srcs[active] = j;
                subBounds[active] = (sb.Offset, sb.Length);
                active++;
            }
        }

        if (active == 0) return;

        if (active == 1)
        {
            int j = srcs[0];
            WholeReadSessionReader r = Reader(views[matchingSources[j]]);
            using NoOpPin pin = r.PinBuffer(subBounds[0].Offset, subBounds[0].Length);
            perAddrBuilder.Add(subTag, pin.Buffer);
            return;
        }

        // Multi-source: streaming N-way merge into a PackedArray driven by the shared
        // loser-tree cursor. CopyCurrentLogicalKey returns lex/BE bytes regardless of the
        // source PackedArray's storage layout, so cross-source min selection on cached
        // keys works at innerKeySize ∈ {2,4,8} BE-stored or auto-LE-stored alike.
        using ArrayPoolList<HsstEnumerator> innerEnumsList = new(active, active);
        using NativeMemoryList<bool> innerHasMoreList = new(active, active);
        HsstEnumerator[] innerEnums = innerEnumsList.UnsafeGetInternalArray();
        Span<bool> innerHasMore = innerHasMoreList.AsSpan();
        Span<byte> keyBuf = stackalloc byte[active * innerKeySize];

        try
        {
            for (int j = 0; j < active; j++)
            {
                WholeReadSessionReader r = Reader(views[matchingSources[srcs[j]]]);
                innerEnums[j] = new HsstEnumerator(in r, new Bound(subBounds[j].Offset, subBounds[j].Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
                if (innerHasMore[j])
                    innerEnums[j].CopyCurrentLogicalKey(in r, keyBuf.Slice(j * innerKeySize, innerKeySize));
            }

            // Compose cursor sourceMap: cursor slot j → views[matchingSources[srcs[j]]].
            int pow2N = (int)BitOperations.RoundUpToPowerOf2((uint)active);
            Span<int> composedMap = stackalloc int[active];
            for (int j = 0; j < active; j++) composedMap[j] = matchingSources[srcs[j]];
            Span<int> matchingBuf = stackalloc int[active];
            Span<int> tree = stackalloc int[2 * pow2N];

            NWayMergeCursor cursor = new(
                innerEnums, innerHasMore, views, composedMap,
                active, innerKeySize, innerKeySize, keyBuf, matchingBuf, tree);

            ref TWriter subWriter = ref perAddrBuilder.BeginValueWrite();
            using HsstPackedArrayBuilder<TWriter> innerBuilder = new(ref subWriter, innerKeySize, NodeRef.Size);

            while (cursor.MoveNext())
            {
                int minIdx = cursor.MinIdx;
                Bound vb = innerEnums[minIdx].CurrentValue;
                WholeReadSessionReader rMin = Reader(views[composedMap[minIdx]]);
                using NoOpPin valPin = rMin.PinBuffer(vb.Offset, vb.Length);
                innerBuilder.Add(cursor.MinKey, valPin.Buffer);
                cursor.AdvanceMatching();
            }

            innerBuilder.Build();
            perAddrBuilder.FinishValueWrite(subTag);
        }
        finally
        {
            for (int j = 0; j < active; j++) innerEnums[j].Dispose();
        }
    }

    /// <summary>
    /// N-way metadata merge: from_block/from_hash from oldest, to_block/to_hash/version from newest.
    /// Injects noderefs=[0x01] and ref_ids from referencedIds set.
    /// Emits in sorted key order.
    /// </summary>
    private static void NWayMetadataMerge<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, ref TWriter writer, SortedSet<ushort> refIds) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        WholeReadSessionReader oldestReader = Reader(views[0]);
        WholeReadSessionReader newestReader = Reader(views[n - 1]);

        // Walk metadata fields directly through the long-aware readers. Each field
        // gets a narrow PinBuffer so the resulting Span is just the field bytes —
        // no wide pin of the entire metadata blob.
        HsstReader<WholeReadSessionReader, NoOpPin> oldestRoot = new(in oldestReader, new Bound(0, oldestReader.Length));
        oldestRoot.TrySeek(PersistedSnapshot.MetadataTag, out Bound oldestMetaScope);
        HsstReader<WholeReadSessionReader, NoOpPin> newestRoot = new(in newestReader, new Bound(0, newestReader.Length));
        newestRoot.TrySeek(PersistedSnapshot.MetadataTag, out Bound newestMetaScope);

        Bound fb = SeekField(in oldestReader, oldestMetaScope, PersistedSnapshot.MetadataFromBlockKey);
        Bound fh = SeekField(in oldestReader, oldestMetaScope, PersistedSnapshot.MetadataFromHashKey);
        Bound tb = SeekField(in newestReader, newestMetaScope, PersistedSnapshot.MetadataToBlockKey);
        Bound th = SeekField(in newestReader, newestMetaScope, PersistedSnapshot.MetadataToHashKey);
        Bound vb = SeekField(in newestReader, newestMetaScope, PersistedSnapshot.MetadataVersionKey);

        using NoOpPin fbPin = oldestReader.PinBuffer(fb.Offset, fb.Length);
        using NoOpPin fhPin = oldestReader.PinBuffer(fh.Offset, fh.Length);
        using NoOpPin tbPin = newestReader.PinBuffer(tb.Offset, tb.Length);
        using NoOpPin thPin = newestReader.PinBuffer(th.Offset, th.Length);
        using NoOpPin vPin = newestReader.PinBuffer(vb.Offset, vb.Length);

        static Bound SeekField(scoped in WholeReadSessionReader r, Bound scope, scoped ReadOnlySpan<byte> key)
        {
            HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, scope);
            hsst.TrySeek(key, out Bound matched);
            return matched;
        }
        ReadOnlySpan<byte> fromBlock = fbPin.Buffer;
        ReadOnlySpan<byte> fromHash = fhPin.Buffer;
        ReadOnlySpan<byte> toBlock = tbPin.Buffer;
        ReadOnlySpan<byte> toHash = thPin.Buffer;
        ReadOnlySpan<byte> version = vPin.Buffer;

        // Build ref_ids value
        byte[] refIdsValue = new byte[refIds.Count * 2];
        int idx = 0;
        foreach (ushort id in refIds)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(refIdsValue.AsSpan(idx * 2, 2), id);
            idx++;
        }

        using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, PersistedSnapshot.MetadataKeyLength);

        // Emit all keys in sorted ASCII order. NUL-padding to 10 bytes preserves the
        // original ASCII sort order:
        // "from_block" < "from_hash\0" < "noderefs\0\0" < "ref_ids\0\0\0" < "to_block\0\0" < "to_hash\0\0\0" < "version\0\0\0"
        builder.Add(PersistedSnapshot.MetadataFromBlockKey, fromBlock);
        builder.Add(PersistedSnapshot.MetadataFromHashKey, fromHash);
        builder.Add(PersistedSnapshot.MetadataNodeRefsKey, [0x01]);
        builder.Add(PersistedSnapshot.MetadataRefIdsKey, refIdsValue);
        builder.Add(PersistedSnapshot.MetadataToBlockKey, toBlock);
        builder.Add(PersistedSnapshot.MetadataToHashKey, toHash);
        builder.Add(PersistedSnapshot.MetadataVersionKey, version);

        builder.Build();
    }

    /// <summary>
    /// Specialised slot merger: outer 30-byte BTree, inner 2-byte BTree (suffix → slot value).
    /// Emits bloom adds inline from the merged stream so the compactor doesn't need a
    /// separate per-source slot-tree walk just to populate the bloom. The merged-stream
    /// adds skip duplicates that newest-wins merge collapses; capacity is sized as the
    /// sum-of-sources count in <see cref="PersistedSnapshotCompactor"/>, which over-sizes
    /// after dedup — harmless (false-positive rate is the same or strictly better).
    /// </summary>
    private static void NWayNestedStreamingSlotMerge<TWriter, TReader, TPin>(
        HsstEnumerator[] outerEnums, Span<bool> outerHasMore, int n,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ref TWriter writer,
        scoped ref HsstBTreeBuilderBuffers slotPrefixBuffers,
        scoped ref HsstBTreeBuilderBuffers slotSuffixBuffers,
        BloomFilter? bloom, ulong addrBloomKey) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        const int OuterKeyLen = 30;
        const int OuterStride = 32;
        using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, ref slotPrefixBuffers, OuterKeyLen, new HsstBTreeOptions { MinSeparatorLength = 4 });

        // Prime cached outer 30-byte keys (stride 32 for alignment). The outerEnums have
        // already been MoveNext'd once by the caller (NWayMergePerAddressHsst); we just
        // copy the first key per still-live source so the cursor can build its tree.
        Span<byte> outerKeyBuf = stackalloc byte[n * OuterStride];
        for (int i = 0; i < n; i++)
        {
            if (!outerHasMore[i]) continue;
            WholeReadSessionReader r = Reader(views[i]);
            outerEnums[i].CopyCurrentLogicalKey(in r, outerKeyBuf.Slice(i * OuterStride, OuterKeyLen));
        }

        int pow2N = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, n));
        Span<int> srcMap = stackalloc int[Math.Max(1, n)];
        for (int i = 0; i < n; i++) srcMap[i] = i;
        Span<int> matchingBuf = stackalloc int[Math.Max(1, n)];
        Span<int> tree = stackalloc int[2 * pow2N];

        NWayMergeCursor cursor = new(
            outerEnums, outerHasMore, views, srcMap, n, OuterKeyLen, OuterStride, outerKeyBuf, matchingBuf, tree);

        while (cursor.MoveNext())
        {
            ReadOnlySpan<byte> minKey = cursor.MinKey;
            int matchCount = cursor.MatchCount;
            ReadOnlySpan<int> matchingSources = cursor.MatchingSources;

            // Bloom is keyed on the 30-byte slot prefix only, so one add per outer
            // bucket covers every slot key in this bucket regardless of matchCount.
            if (bloom is not null)
                bloom.Add(PersistedSnapshotBloomBuilder.SlotPrefixKey(addrBloomKey, minKey));

            if (matchCount == 1)
            {
                // Single-source fast path: byte-copy the source's slot-suffix HSST blob
                // verbatim. HSST internal pointers are blob-relative, so the relocated
                // blob stays readable at the destination writer position. Streamed via
                // the long-aware IByteBufferWriter.Copy so >2 GiB suffix HSSTs stay safe.
                int srcIdx = matchingSources[0];
                Bound vb = outerEnums[srcIdx].CurrentValue;
                WholeReadSessionReader srcReader = Reader(views[srcIdx]);
                ref TWriter innerWriter = ref builder.BeginValueWrite();
                IByteBufferWriter.Copy<TWriter, WholeReadSessionReader, NoOpPin>(
                    ref innerWriter, in srcReader, vb);
                builder.FinishValueWrite(minKey);
            }
            else
            {
                ref TWriter innerWriter = ref builder.BeginValueWrite();
                NWayInnerSlotMerge<TWriter, TReader, TPin>(
                    outerEnums, matchingSources, matchCount, views,
                    ref innerWriter, ref slotSuffixBuffers);
                builder.FinishValueWrite(minKey);
            }

            cursor.AdvanceMatching();
        }

        builder.Build();
    }

    /// <summary>
    /// Inner BTree merge for the slot path. Same structure as <see cref="NWayInnerMerge{TWriter, TReader, TPin}"/>
    /// but with a fixed 2-byte inner key. The slot bloom is keyed on the 30-byte outer
    /// prefix (added once per bucket by the caller), so this inner pass does not touch
    /// the bloom.
    /// </summary>
    private static void NWayInnerSlotMerge<TWriter, TReader, TPin>(
        HsstEnumerator[] outerEnums, scoped ReadOnlySpan<int> matchingSources, int matchCount,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ref TWriter writer,
        scoped ref HsstBTreeBuilderBuffers slotSuffixBuffers) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        const int InnerKeyLen = 2;
        using ArrayPoolList<HsstEnumerator> innerEnums = new(matchCount, matchCount);
        using NativeMemoryList<bool> innerHasMore = new(matchCount, matchCount);
        Span<byte> keyBuf = stackalloc byte[matchCount * InnerKeyLen];

        try
        {
            for (int j = 0; j < matchCount; j++)
            {
                int srcIdx = matchingSources[j];
                Bound vb = outerEnums[srcIdx].CurrentValue;
                WholeReadSessionReader r = Reader(views[srcIdx]);
                innerEnums[j] = new HsstEnumerator(in r, new Bound(vb.Offset, vb.Length));
                innerHasMore[j] = innerEnums[j].MoveNext(in r);
                if (innerHasMore[j])
                    innerEnums[j].CopyCurrentLogicalKey(in r, keyBuf.Slice(j * InnerKeyLen, InnerKeyLen));
            }

            int pow2N = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, matchCount));
            Span<int> matchingBuf = stackalloc int[Math.Max(1, matchCount)];
            Span<int> tree = stackalloc int[2 * pow2N];

            // sourceMap = matchingSources: cursor slot j → views[matchingSources[j]].
            NWayMergeCursor cursor = new(
                innerEnums.UnsafeGetInternalArray(), innerHasMore.AsSpan(),
                views, matchingSources, matchCount, InnerKeyLen, InnerKeyLen, keyBuf, matchingBuf, tree);

            using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, ref slotSuffixBuffers, InnerKeyLen, new HsstBTreeOptions { MinSeparatorLength = 2 });

            while (cursor.MoveNext())
            {
                int minIdx = cursor.MinIdx;
                Bound vb = innerEnums[minIdx].CurrentValue;
                WholeReadSessionReader rMin = Reader(views[matchingSources[minIdx]]);
                using NoOpPin valPin = rMin.PinBuffer(vb.Offset, vb.Length);
                builder.Add(cursor.MinKey, valPin.Buffer);
                cursor.AdvanceMatching();
            }

            builder.Build();
        }
        finally
        {
            for (int j = 0; j < matchCount; j++) innerEnums[j].Dispose();
        }
    }

    /// <summary>
    /// Walk the outer 30-byte slot-prefix HSST at <paramref name="slotScope"/> and add
    /// one bloom entry per prefix bucket. The inner 2-byte suffix HSST is not walked —
    /// the bloom is keyed on the 30-byte prefix only (see
    /// <see cref="PersistedSnapshotBloomBuilder.SlotPrefixKey"/>). Used by the
    /// matchCount==1 / slotSourceCount==1 byte-copy fast paths.
    /// </summary>
    private static void AddSlotKeysToBloom<TReader, TPin>(
        scoped in TReader reader, Bound slotScope, ulong addrKey, BloomFilter bloom)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        Span<byte> prefix = stackalloc byte[30];
        HsstEnumerator<TReader, TPin> outerEnum = new(in reader, slotScope);
        while (outerEnum.MoveNext(in reader))
        {
            outerEnum.CopyCurrentLogicalKey(in reader, prefix);
            bloom.Add(PersistedSnapshotBloomBuilder.SlotPrefixKey(addrKey, prefix));
        }
        outerEnum.Dispose();
    }
}
