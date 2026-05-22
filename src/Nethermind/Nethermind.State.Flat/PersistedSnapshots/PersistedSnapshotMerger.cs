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
    /// N-way merge of N persisted snapshots (oldest-first) into <paramref name="writer"/>.
    /// Callers (the compactor in production, the test/benchmark helpers otherwise) own the
    /// session lifecycle: open one <see cref="WholeReadSession"/> per source up front, pass
    /// the raw views in here, dispose the sessions after the merge returns. One mmap +
    /// <c>MADV_NORMAL</c> on open and one <c>MADV_DONTNEED</c> on close per source — the
    /// per-column helpers walk these pre-opened views and do not re-open anything inside.
    /// </summary>
    internal static void NWayMergeSnapshotsWithViews<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, ref TWriter writer,
        BloomFilter bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(bloom);
        // All snapshots are blob-backed (values in trie columns are NodeRefs), so we can
        // merge them directly without any Full→Linked pre-conversion stage. Columns are
        // emitted in strictly descending tag order, as the outer DenseByteIndex requires:
        // storage-trie (0x05), state-fallback (0x04), state-node (0x03), state-top-nodes
        // (0x02), per-address (0x01), metadata (0x00). Column 0x01 carries per-address
        // {account, SD, slots} keyed by raw Address. Column 0x05 carries per-addressHash
        // {storage-trie top/compact/fallback}.
        using HsstDenseByteIndexBuilder<TWriter> outerBuilder = new(ref writer);

        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayMergeStorageTrieColumn<TWriter, TReader, TPin>(views, PersistedSnapshotTags.StorageTrieColumnTag, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StorageTrieColumnTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayPackedArrayMerge<TWriter, TReader, TPin>(views, PersistedSnapshotTags.StateNodeFallbackTag, ref valueWriter, keySize: 33, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StateNodeFallbackTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayPackedArrayMerge<TWriter, TReader, TPin>(views, PersistedSnapshotTags.StateNodeTag, ref valueWriter, keySize: 8, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StateNodeTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayPackedArrayMerge<TWriter, TReader, TPin>(views, PersistedSnapshotTags.StateTopNodesTag, ref valueWriter, keySize: 4, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StateTopNodesTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayMergePerAddressColumn<TWriter, TReader, TPin>(views, PersistedSnapshotTags.AccountColumnTag, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.AccountColumnTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayMetadataMerge<TWriter, TReader, TPin>(views, ref valueWriter);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.MetadataTag);
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
    private static void NWayPackedArrayMerge<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, byte[] tag, ref TWriter writer,
        int keySize, BloomFilter bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        using ArrayPoolList<HsstEnumerator> enums = new(n, n);
        using NativeMemoryListRef<bool> hasMore = new(n, n);
        // Cache each source's current logical key once per MoveNext so the O(log N) cursor
        // and O(N) match-detection scans don't redo CopyCurrentLogicalKey per output key.
        int keyStride = Math.Max(1, keySize);
        using NativeMemoryListRef<byte> keyBufList = new(n * keyStride, n * keyStride);
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
                bloom.Add(PersistedSnapshotBloomBuilder.StatePathKey(cursor.MinKey));

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
    /// N-way merge of the per-address column (tag 0x01) across N snapshots.
    /// Outer: raw 20-byte Address keys (minSep=4). A single matching source
    /// whose per-address HSST entry (key + value) fits one page and can be page-
    /// aligned at the current writer position byte-copies through
    /// <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}.TryAddAligned"/>
    /// (HSST internal pointers are HSST-relative, so a relocation stays readable);
    /// larger entries, unalignable positions, and any multi-source collision fall
    /// through to <see cref="NWayMergePerAddressHsst"/>, which re-emits per sub-tag.
    /// Per-address inner sub-tags are 0x00 (account RLP), 0x01 (self-destruct),
    /// 0x02 (slots). Storage-trie nodes live in column 0x05 keyed by addressHash
    /// and are merged separately by <see cref="NWayMergeStorageTrieColumn"/>.
    /// </summary>
    private static void NWayMergePerAddressColumn<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, byte[] tag, ref TWriter writer, BloomFilter bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        using ArrayPoolList<HsstEnumerator> enumsList = new(n, n);
        using NativeMemoryListRef<bool> hasMoreList = new(n, n);
        HsstEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        Span<bool> hasMore = hasMoreList.AsSpan();

        // Cache each source's current 20-byte Address key (stride 32 with room).
        const int KeyStride = 32;
        const int AddrKeyLen = PersistedSnapshotTags.AddressKeyLength;
        Span<byte> keyBuf = stackalloc byte[n * KeyStride];

        // Reusable work buffers for the per-address slot prefix/suffix HSST builders.
        // Declared at column scope so the rentals stay alive across every merged
        // address — the prefix builder is created once per address and the suffix
        // builder once per prefix group per address, so churn dominates otherwise.
        // Plain locals (not `using`) so they can be passed by ref through the call
        // chain into the builder constructors.
        HsstBTreeBuilderBuffers slotPrefixBuffers = new();

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

            // builder is passed to ReaddAddressHsst by ref, so it can't be a `using`
            // declaration (the compiler refuses ref to using-variables). Manage its
            // disposal with a try/finally instead.
            HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, PersistedSnapshotTags.AddressKeyLength);
            try
            {
                while (cursor.MoveNext())
                {
                    ReadOnlySpan<byte> minKey = cursor.MinKey;
                    int matchCount = cursor.MatchCount;
                    ReadOnlySpan<int> matchingSources = cursor.MatchingSources;
                    ulong addrKey = MemoryMarshal.Read<ulong>(minKey);
                    bloom.Add(addrKey);

                    // Single-source direct-copy fast path: pin the source per-address
                    // HSST and try to add it page-aligned through the destination
                    // builder. Falls through to the rebuild path if the entry can't
                    // fit on one page or the alignment pad would be too large.
                    if (matchCount == 1)
                    {
                        int srcIdx = matchingSources[0];
                        Bound vb = enums[srcIdx].CurrentValue;
                        // Fast-fail short-circuit: NoOpPin.PinBuffer casts size to int
                        // and would throw on a >2 GiB blob, so skip the pin attempt
                        // for obviously-disqualified sizes. TryAddAligned still does
                        // its own precise entry-size check internally.
                        if (vb.Length <= PageLayout.PageSize)
                        {
                            WholeReadSessionReader srcReader = Reader(views[srcIdx]);
                            using NoOpPin blobPin = srcReader.PinBuffer(vb.Offset, vb.Length);
                            if (builder.TryAddAligned(minKey, blobPin.Buffer))
                            {
                                // Walk the source's per-address blob to add bloom keys for
                                // slots. Storage-trie sub-tags no longer live here — those
                                // are walked by the column-0x05 merger.
                                HsstReader<WholeReadSessionReader, NoOpPin> outer = new(in srcReader, vb);
                                if (outer.TrySeek(PersistedSnapshotTags.SlotSubTag, out Bound slotBound))
                                    AddSlotKeysToBloom<WholeReadSessionReader, NoOpPin>(in srcReader, slotBound, addrKey, bloom);

                                cursor.AdvanceMatching();
                                continue;
                            }
                        }
                    }

                    // Rebuild path: resolve every source's per-address bounds and sub-tag
                    // bounds, then stream the merged DenseByteIndex through
                    // NWayMergePerAddressHsst. Used for any multi-source collision and
                    // for single-source blobs that exceed a page (re-emitting per sub-tag
                    // keeps the result page-aligned where the verbatim copy could not).
                    using NativeMemoryListRef<(long Offset, long Length)> perAddrBoundsList = new(matchCount, matchCount);
                    Span<(long Offset, long Length)> perAddrBounds = perAddrBoundsList.AsSpan();
                    for (int j = 0; j < matchCount; j++)
                    {
                        Bound vb = enums[matchingSources[j]].CurrentValue;
                        perAddrBounds[j] = (vb.Offset, vb.Length);
                    }

                    using NativeMemoryListRef<Bound> subTagBoundsList = new(matchCount * PersistedSnapshotTags.PerAddrSubTagCount, matchCount * PersistedSnapshotTags.PerAddrSubTagCount);
                    Span<Bound> subTagBounds = subTagBoundsList.AsSpan();
                    for (int j = 0; j < matchCount; j++)
                    {
                        WholeReadSessionReader r = Reader(views[matchingSources[j]]);
                        HsstDenseByteIndexReader.TryResolveAll<WholeReadSessionReader, NoOpPin>(
                            in r,
                            new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length),
                            subTagBounds.Slice(j * PersistedSnapshotTags.PerAddrSubTagCount, PersistedSnapshotTags.PerAddrSubTagCount));
                    }

                    ref TWriter perAddrWriter = ref builder.BeginValueWrite();
                    NWayMergePerAddressHsst<TWriter, TReader, TPin>(
                        matchingSources, matchCount, views,
                        ref perAddrWriter, ref slotPrefixBuffers,
                        subTagBounds,
                        bloom, addrKey);
                    builder.FinishValueWrite(minKey);

                    cursor.AdvanceMatching();
                }

                builder.Build();
            }
            finally
            {
                builder.Dispose();
            }
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i].Dispose();
            slotPrefixBuffers.Dispose();
        }
    }

    /// <summary>
    /// N-way merge of the storage-trie column (tag 0x05) across N snapshots.
    /// Outer: 20-byte addressHash prefix keys. For each merged addressHash the inner
    /// DenseByteIndex carries sub-tags 0x00 (top), 0x01 (compact), 0x02 (fallback) —
    /// each a nested HSST keyed by encoded TreePath with 6-byte NodeRef values.
    /// Single-source matches with a page-fittable, page-alignable blob byte-copy
    /// through TryAddAligned and walk bloom keys via AddStorageTrieKeysToBloom; any
    /// multi-source collision and any unalignable single-source blob fall through
    /// to a per-addressHash inner rebuild that re-emits each sub-tag (descending
    /// 0x02 → 0x01 → 0x00) via the shared <see cref="MergeStorageTrieSubTag"/>
    /// helper, which already streams the inner-BTree merge.
    /// </summary>
    private static void NWayMergeStorageTrieColumn<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, byte[] tag, ref TWriter writer, BloomFilter bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        using ArrayPoolList<HsstEnumerator> enumsList = new(n, n);
        using NativeMemoryListRef<bool> hasMoreList = new(n, n);
        HsstEnumerator[] enums = enumsList.UnsafeGetInternalArray();
        Span<bool> hasMore = hasMoreList.AsSpan();

        const int KeyStride = 32;
        const int AddrKeyLen = PersistedSnapshotTags.AddressHashPrefixLength;
        Span<byte> keyBuf = stackalloc byte[n * KeyStride];

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

            HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, PersistedSnapshotTags.AddressHashPrefixLength);
            try
            {
                while (cursor.MoveNext())
                {
                    ReadOnlySpan<byte> minKey = cursor.MinKey;
                    int matchCount = cursor.MatchCount;
                    ReadOnlySpan<int> matchingSources = cursor.MatchingSources;
                    ulong addrKey = MemoryMarshal.Read<ulong>(minKey);

                    if (matchCount == 1)
                    {
                        int srcIdx = matchingSources[0];
                        Bound vb = enums[srcIdx].CurrentValue;
                        if (vb.Length <= PageLayout.PageSize)
                        {
                            WholeReadSessionReader srcReader = Reader(views[srcIdx]);
                            using NoOpPin blobPin = srcReader.PinBuffer(vb.Offset, vb.Length);
                            if (builder.TryAddAligned(minKey, blobPin.Buffer))
                            {
                                HsstReader<WholeReadSessionReader, NoOpPin> outer = new(in srcReader, vb);
                                Bound outerRoot = outer.GetBound();
                                if (outer.TrySeek(PersistedSnapshotTags.StorageTopSubTag, out Bound stb))
                                    AddStorageTrieKeysToBloom<WholeReadSessionReader, NoOpPin>(in srcReader, stb, addrKey, bloom);
                                outer.SetBound(outerRoot);
                                if (outer.TrySeek(PersistedSnapshotTags.StorageCompactSubTag, out Bound scb))
                                    AddStorageTrieKeysToBloom<WholeReadSessionReader, NoOpPin>(in srcReader, scb, addrKey, bloom);
                                outer.SetBound(outerRoot);
                                if (outer.TrySeek(PersistedSnapshotTags.StorageFallbackSubTag, out Bound sfb))
                                    AddStorageTrieKeysToBloom<WholeReadSessionReader, NoOpPin>(in srcReader, sfb, addrKey, bloom);

                                cursor.AdvanceMatching();
                                continue;
                            }
                        }
                    }

                    // Rebuild path: resolve every source's per-addressHash sub-tag bounds,
                    // then stream the merged inner DenseByteIndex via MergeStorageTrieSubTag.
                    using NativeMemoryListRef<(long Offset, long Length)> perAddrBoundsList = new(matchCount, matchCount);
                    Span<(long Offset, long Length)> perAddrBounds = perAddrBoundsList.AsSpan();
                    for (int j = 0; j < matchCount; j++)
                    {
                        Bound vb = enums[matchingSources[j]].CurrentValue;
                        perAddrBounds[j] = (vb.Offset, vb.Length);
                    }

                    using NativeMemoryListRef<Bound> subTagBoundsList = new(matchCount * PersistedSnapshotTags.StorageTrieSubTagCount, matchCount * PersistedSnapshotTags.StorageTrieSubTagCount);
                    Span<Bound> subTagBounds = subTagBoundsList.AsSpan();
                    for (int j = 0; j < matchCount; j++)
                    {
                        WholeReadSessionReader r = Reader(views[matchingSources[j]]);
                        HsstDenseByteIndexReader.TryResolveAll<WholeReadSessionReader, NoOpPin>(
                            in r,
                            new Bound(perAddrBounds[j].Offset, perAddrBounds[j].Length),
                            subTagBounds.Slice(j * PersistedSnapshotTags.StorageTrieSubTagCount, PersistedSnapshotTags.StorageTrieSubTagCount));
                    }

                    ref TWriter perAddrWriter = ref builder.BeginValueWrite();
                    HsstDenseByteIndexBuilder<TWriter> perAddrBuilder = new(ref perAddrWriter);
                    try
                    {
                        // Emit descending 0x02 (fallback) → 0x01 (compact) → 0x00 (top).
                        MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, views, subTagBounds,
                            ref perAddrBuilder, PersistedSnapshotTags.StorageFallbackSubTag,
                            subTagIdx: PersistedSnapshotTags.StorageFallbackSubTag[0], innerKeySize: 33, perSourceStride: PersistedSnapshotTags.StorageTrieSubTagCount,
                            bloom, addrKey);
                        MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, views, subTagBounds,
                            ref perAddrBuilder, PersistedSnapshotTags.StorageCompactSubTag,
                            subTagIdx: PersistedSnapshotTags.StorageCompactSubTag[0], innerKeySize: 8, perSourceStride: PersistedSnapshotTags.StorageTrieSubTagCount,
                            bloom, addrKey);
                        MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, views, subTagBounds,
                            ref perAddrBuilder, PersistedSnapshotTags.StorageTopSubTag,
                            subTagIdx: PersistedSnapshotTags.StorageTopSubTag[0], innerKeySize: 4, perSourceStride: PersistedSnapshotTags.StorageTrieSubTagCount,
                            bloom, addrKey);
                        perAddrBuilder.Build();
                    }
                    finally
                    {
                        perAddrBuilder.Dispose();
                    }
                    builder.FinishValueWrite(minKey);

                    cursor.AdvanceMatching();
                }

                builder.Build();
            }
            finally
            {
                builder.Dispose();
            }
        }
        finally
        {
            for (int i = 0; i < n; i++) enums[i].Dispose();
        }
    }

    /// <summary>
    /// N-way merge of per-address HSSTs from M sources (oldest-first by matchingSources order).
    /// All three column-0x01 inner sub-tags emitted in <b>descending</b> byte order so the
    /// DenseByteIndex builder accepts them (writer streams high-tag → low-tag):
    /// - 0x02 Slots: find newest destruct barrier, merge slots from barrier..M-1 via nested streaming merge
    /// - 0x01 SelfDestruct: iterate 0..M-1, apply TryAdd semantics
    /// - 0x00 Account: newest wins (walk M-1..0, first with AccountSubTag)
    /// Storage-trie nodes for the matching addressHash live in column 0x05 and are merged
    /// independently by <see cref="NWayMergeStorageTrieColumn"/>.
    /// </summary>
    private static void NWayMergePerAddressHsst<TWriter, TReader, TPin>(
        scoped ReadOnlySpan<int> matchingSources, int matchCount,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ref TWriter writer,
        ref HsstBTreeBuilderBuffers slotPrefixBuffers,
        scoped ReadOnlySpan<Bound> subTagBounds,
        BloomFilter bloom, ulong addrBloomKey = 0) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        // perAddrBuilder is passed to several helpers by ref, so it can't be a `using`
        // declaration (the compiler refuses ref to using-variables). Manage its disposal
        // with a try/finally instead.
        HsstDenseByteIndexBuilder<TWriter> perAddrBuilder = new(ref writer);
        try
        {
            // Find newest destruct barrier: newest j where SelfDestructSubTag is present and
            // marks "destructed" ([0x00]). With DenseByteIndex per-address encoding, sub-tag
            // values are presence-marked: length 0 = absent, [0x00] = destructed, [0x01] = new.
            int sdTag = PersistedSnapshotTags.SelfDestructSubTag[0];
            int destructBarrier = -1;
            for (int j = 0; j < matchCount; j++)
            {
                Bound sdb = subTagBounds[j * PersistedSnapshotTags.PerAddrSubTagCount + sdTag];
                if (sdb.Length != 1) continue;
                WholeReadSessionReader r = Reader(views[matchingSources[j]]);
                using NoOpPin sdPin = r.PinBuffer(sdb.Offset, 1);
                if (sdPin.Buffer[0] == PersistedSnapshotTags.SelfDestructDestructedMarkerByte)
                    destructBarrier = j;
            }

            // Sub-tag 0x02: Slots — emitted first so the per-address DenseByteIndex receives
            // tags in strictly descending order. Merge slots only from max(0, destructBarrier)
            // ..matchCount-1. Collect the active slot sources, then early-return for 0 sources
            // (no emit) or run the outer/inner BTree streaming merge through
            // NWayNestedStreamingSlotMerge for any positive count. We do not byte-copy a
            // single-source slot blob through perAddrBuilder here: the dense byte index does
            // not page-align its values, so re-emitting through the inner BTree builder (which
            // does align) keeps the slot HSST on its own page.
            {
                int slotStart = Math.Max(0, destructBarrier);
                int slotTag = PersistedSnapshotTags.SlotSubTag[0];
                int slotSourceCount = 0;
                int slotCapacity = matchCount - slotStart;
                using NativeMemoryListRef<int> slotSourcesList = new(slotCapacity, slotCapacity);
                using NativeMemoryListRef<(long Offset, long Length)> slotBoundsList = new(slotCapacity, slotCapacity);
                Span<int> slotSources = slotSourcesList.AsSpan();
                Span<(long Offset, long Length)> slotBounds = slotBoundsList.AsSpan();
                for (int j = slotStart; j < matchCount; j++)
                {
                    Bound slotBound = subTagBounds[j * PersistedSnapshotTags.PerAddrSubTagCount + slotTag];
                    if (slotBound.Length > 0)
                    {
                        slotSources[slotSourceCount] = matchingSources[j];
                        slotBounds[slotSourceCount] = (slotBound.Offset, slotBound.Length);
                        slotSourceCount++;
                    }
                }

                if (slotSourceCount > 0)
                {
                    using ArrayPoolList<HsstEnumerator> slotEnumsList = new(slotSourceCount, slotSourceCount);
                    using NativeMemoryListRef<bool> slotHasMoreList = new(slotSourceCount, slotSourceCount);
                    using NativeMemoryListRef<(IntPtr Ptr, long Len)> slotViewsList = new(slotSourceCount, slotSourceCount);
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
                            ref slotPrefixBuffers,
                            bloom, addrBloomKey);
                        perAddrBuilder.FinishValueWrite(PersistedSnapshotTags.SlotSubTag);
                    }
                    finally
                    {
                        for (int j = 0; j < slotSourceCount; j++) slotEnums[j].Dispose();
                    }
                }
            }

            // Sub-tag 0x01: SelfDestruct — iterate 0..M-1, apply TryAdd semantics. Presence
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
                    Bound sdb = subTagBounds[j * PersistedSnapshotTags.PerAddrSubTagCount + sdTag];
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
                        if (firstBytePin.Buffer[0] == PersistedSnapshotTags.SelfDestructDestructedMarkerByte)
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
                    perAddrBuilder.Add(PersistedSnapshotTags.SelfDestructSubTag, sdPin.Buffer);
                }
            }

            // Sub-tag 0x00: Account — newest wins (walk M-1..0, first present (length>0)).
            // Emitted last so the hot Account blob lands adjacent to the DenseByteIndex
            // Ends[] trailer.
            {
                int acctTag = PersistedSnapshotTags.AccountSubTag[0];
                for (int j = matchCount - 1; j >= 0; j--)
                {
                    Bound ab = subTagBounds[j * PersistedSnapshotTags.PerAddrSubTagCount + acctTag];
                    if (ab.Length == 0) continue;
                    WholeReadSessionReader r = Reader(views[matchingSources[j]]);
                    using NoOpPin acctPin = r.PinBuffer(ab.Offset, ab.Length);
                    perAddrBuilder.Add(PersistedSnapshotTags.AccountSubTag, acctPin.Buffer);
                    break;
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
    /// Outer 30-byte slot-prefix BTree streaming merge across M slot-bearing sources, with
    /// the inner 2-byte suffix BTree merge inlined per bucket. Per outer bucket, emits one
    /// bloom add (keyed on the 30-byte prefix); when only one source matches an outer
    /// key and the source suffix HSST entry fits and can be page-aligned, pins the source
    /// blob and adds it whole through the outer builder via
    /// <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}.TryAddAligned"/>, skipping the
    /// inner merge entirely. Otherwise (multi-source bucket, or single-source with
    /// unalignable suffix) the inner merge runs. Caller is responsible for: collecting the
    /// slot-bearing sources from per-address sub-tag 0x02, opening the slot enums, and
    /// wrapping this call in BeginValueWrite/FinishValueWrite on its outer builder.
    /// </summary>
    private static void NWayNestedStreamingSlotMerge<TWriter, TReader, TPin>(
        HsstEnumerator[] outerEnums, Span<bool> outerHasMore, int n,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ref TWriter writer,
        scoped ref HsstBTreeBuilderBuffers slotPrefixBuffers,
        BloomFilter bloom, ulong addrBloomKey) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        const int OuterKeyLen = 30;
        const int OuterStride = 32;
        const int InnerKeyLen = 2;
        using HsstBTreeBuilder<TWriter, TReader, TPin> outerBuilder = new(ref writer, ref slotPrefixBuffers, OuterKeyLen, keyFirst: true);
        // Per-prefix staging buffer for the sub-slot HSST. The outer BTree is built
        // key-first, so its outer entry layout requires the value length up front —
        // each sub-slot must be fully materialised in this buffer before Add. Reused
        // across prefix iterations via Reset() to amortize the backing allocation.
        using PooledByteBufferWriter innerStaging = new(4096);

        // Prime outer 30-byte keys (stride 32 for alignment). The outerEnums have already
        // been MoveNext'd once by the caller; we just copy the first key per still-live
        // source so the cursor can build its tree.
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
        Span<int> outerMatchingBuf = stackalloc int[Math.Max(1, n)];
        Span<int> outerTree = stackalloc int[2 * pow2N];

        // Pre-allocate inner-merge working buffers sized to the worst case (innerN == n),
        // sliced down per outer iteration. Hoisted out of the cursor loop so the stackalloc
        // doesn't repeatedly grow the frame (CA2014).
        Span<byte> innerKeyBuf = stackalloc byte[Math.Max(1, n) * InnerKeyLen];
        Span<int> innerMatchingBuf = stackalloc int[Math.Max(1, n)];
        Span<int> innerTree = stackalloc int[2 * pow2N];

        // Reusable 32-byte slot-key scratch for per-slot bloom adds: outerKey (30 bytes)
        // populates [0,30); per-slot innerSuffix (2 bytes) populates [30,32). Allocated once
        // here so the per-slot bloom path is allocation-free.
        Span<byte> slotKeyBuf = stackalloc byte[32];

        // Inner-merge scratch buffers — hoisted once and Clear()ed between multi-source
        // prefix groups so both the ArrayPool rents and the ArrayPoolList wrappers reuse.
        // Sized at construction for a typical small group; the lists grow internally as needed.
        using ArrayPoolList<byte> scratchValues = new(512);
        using ArrayPoolList<byte> scratchKeys = new(Math.Max(1, n) * InnerKeyLen);
        using ArrayPoolList<int> scratchLens = new(Math.Max(1, n));

        NWayMergeCursor outerCursor = new(
            outerEnums, outerHasMore, views, srcMap,
            n, OuterKeyLen, OuterStride, outerKeyBuf, outerMatchingBuf, outerTree);

        while (outerCursor.MoveNext())
        {
            ReadOnlySpan<byte> outerKey = outerCursor.MinKey;
            int outerMatchCount = outerCursor.MatchCount;
            ReadOnlySpan<int> outerMatches = outerCursor.MatchingSources;

            outerKey.CopyTo(slotKeyBuf[..OuterKeyLen]);

            // 1-matching-source fast path: pin the source's suffix HSST blob and try
            // to add it page-aligned through the outer builder. HSST internal pointers
            // are blob-relative so the relocated blob stays readable. The bloom walk
            // reads the source bytes directly. Falls through to the inner-merge
            // rebuild below if the entry can't fit on one page or the alignment pad
            // would exceed the threshold.
            if (outerMatchCount == 1)
            {
                int srcIdx = outerMatches[0];
                Bound vb = outerEnums[srcIdx].CurrentValue;
                WholeReadSessionReader srcReader = Reader(views[srcIdx]);
                using NoOpPin suffixPin = srcReader.PinBuffer(vb.Offset, vb.Length);
                if (outerBuilder.TryAddAligned(outerKey, suffixPin.Buffer))
                {
                    // The outer entry's value is a keys-first TwoByteSlotValue / -Large
                    // sub-slot blob — front-dispatch on byte 0, no tail read.
                    HsstEnumerator<WholeReadSessionReader, NoOpPin> suffixEnum =
                        HsstEnumerator<WholeReadSessionReader, NoOpPin>.CreateTwoByteSlot(in srcReader, vb);
                    while (suffixEnum.MoveNext(in srcReader))
                    {
                        suffixEnum.CopyCurrentLogicalKey(in srcReader, slotKeyBuf.Slice(OuterKeyLen, InnerKeyLen));
                        bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, slotKeyBuf));
                    }
                    suffixEnum.Dispose();
                    outerCursor.AdvanceMatching();
                    continue;
                }
            }

            {
                // Rebuild path: inner 2-byte BTree streaming merge driven by a second
                // cursor over the matched-source subset. Handles >1 matching sources
                // and the N=1 fall-through case when TryAddAligned above couldn't fit
                // the source blob on one page. Working buffers
                // (innerKeyBuf/innerMatchingBuf/innerTree) are pre-allocated above and
                // sliced to the actual inner-source count per iteration.
                int innerN = outerMatchCount;
                using ArrayPoolList<HsstEnumerator> innerEnumsList = new(innerN, innerN);
                using NativeMemoryListRef<bool> innerHasMoreList = new(innerN, innerN);
                HsstEnumerator[] innerEnums = innerEnumsList.UnsafeGetInternalArray();
                Span<bool> innerHasMore = innerHasMoreList.AsSpan();
                Span<byte> iKeyBuf = innerKeyBuf[..(innerN * InnerKeyLen)];
                try
                {
                    for (int k = 0; k < innerN; k++)
                    {
                        int srcIdx = outerMatches[k];
                        Bound vb = outerEnums[srcIdx].CurrentValue;
                        WholeReadSessionReader r = Reader(views[srcIdx]);
                        // Outer entry value is a keys-first TwoByteSlotValue / -Large blob.
                        innerEnums[k] = HsstEnumerator.CreateTwoByteSlot(in r, new Bound(vb.Offset, vb.Length));
                        innerHasMore[k] = innerEnums[k].MoveNext(in r);
                        if (innerHasMore[k])
                            innerEnums[k].CopyCurrentLogicalKey(in r, iKeyBuf.Slice(k * InnerKeyLen, InnerKeyLen));
                    }

                    int innerPow2N = (int)BitOperations.RoundUpToPowerOf2((uint)innerN);
                    Span<int> iMatchingBuf = innerMatchingBuf[..innerN];
                    Span<int> iTree = innerTree[..(2 * innerPow2N)];

                    // sourceMap = outerMatches: inner cursor slot k → views[outerMatches[k]].
                    NWayMergeCursor innerCursor = new(
                        innerEnums, innerHasMore, views, outerMatches,
                        innerN, InnerKeyLen, InnerKeyLen, iKeyBuf, iMatchingBuf, iTree);

                    // Buffer the merged stream so we can size it and pick the inner format
                    // afterward. TwoByteSlotValue caps the data region at ushort.MaxValue;
                    // BTree handles anything larger. Per-prefix-group payloads are tiny in
                    // practice (a handful of slots × ≤32 bytes), so the buffering cost
                    // beats the format-choice trade-off. Scratch lists are hoisted; reuse
                    // their backing arrays across outer iterations.
                    scratchValues.Clear();
                    scratchKeys.Clear();
                    scratchLens.Clear();

                    while (innerCursor.MoveNext())
                    {
                        int innerMinIdx = innerCursor.MinIdx;
                        Bound vb = innerEnums[innerMinIdx].CurrentValue;
                        WholeReadSessionReader rMin = Reader(views[outerMatches[innerMinIdx]]);
                        using NoOpPin valPin = rMin.PinBuffer(vb.Offset, vb.Length);
                        ReadOnlySpan<byte> innerKey = innerCursor.MinKey;
                        innerKey.CopyTo(slotKeyBuf.Slice(OuterKeyLen, InnerKeyLen));
                        bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, slotKeyBuf));
                        scratchValues.AddRange(valPin.Buffer);
                        scratchKeys.AddRange(innerKey);
                        scratchLens.Add((int)vb.Length);
                        innerCursor.AdvanceMatching();
                    }

                    innerStaging.Reset();
                    ref PooledByteBufferWriter.Writer stagingWriter = ref innerStaging.GetWriter();
                    ReadOnlySpan<byte> mergedValues = scratchValues.AsSpan();
                    ReadOnlySpan<byte> mergedKeys = scratchKeys.AsSpan();
                    ReadOnlySpan<int> mergedLens = scratchLens.AsSpan();
                    if (HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(mergedValues.Length))
                    {
                        using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> innerBuilder = new(ref stagingWriter);
                        int valOff = 0;
                        for (int i = 0; i < mergedLens.Length; i++)
                        {
                            innerBuilder.Add(mergedKeys.Slice(i * InnerKeyLen, InnerKeyLen), mergedValues.Slice(valOff, mergedLens[i]));
                            valOff += mergedLens[i];
                        }
                        innerBuilder.Build();
                    }
                    else
                    {
                        using HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer> innerBuilder = new(ref stagingWriter);
                        int valOff = 0;
                        for (int i = 0; i < mergedLens.Length; i++)
                        {
                            innerBuilder.Add(mergedKeys.Slice(i * InnerKeyLen, InnerKeyLen), mergedValues.Slice(valOff, mergedLens[i]));
                            valOff += mergedLens[i];
                        }
                        innerBuilder.Build();
                    }
                    outerBuilder.Add(outerKey, innerStaging.WrittenSpan);
                }
                finally
                {
                    for (int k = 0; k < innerN; k++) innerEnums[k].Dispose();
                }
            }

            outerCursor.AdvanceMatching();
        }

        outerBuilder.Build();
    }

    /// <summary>
    /// Merge a single storage-trie sub-tag (0x00 top, 0x01 compact, or 0x02 fallback) across the M
    /// matching per-address sources into <paramref name="perAddrBuilder"/>. Each source's
    /// sub-tag value is an inner HSST(BTree) keyed by encoded TreePath; values are
    /// NodeRefs (all snapshots are blob-backed by the time the N-way merge runs). When
    /// only one source has the sub-tag, copies its bytes verbatim. With multiple sources,
    /// runs an N-way streaming merge into a fixed-size <see cref="HsstPackedArrayBuilder{TWriter}"/>
    /// (innerKeySize → NodeRef.Size). Newest wins on key collision; storage trie nodes
    /// are content-addressable so duplicate keys carry identical NodeRefs in practice.
    /// </summary>
    private static void MergeStorageTrieSubTag<TWriter, TReader, TPin>(
        scoped ReadOnlySpan<int> matchingSources, int matchCount,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        scoped ReadOnlySpan<Bound> subTagBounds,
        ref HsstDenseByteIndexBuilder<TWriter> perAddrBuilder,
        byte[] subTag,
        int subTagIdx,
        int innerKeySize,
        int perSourceStride,
        BloomFilter bloom,
        ulong addrKey) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        using NativeMemoryListRef<int> srcsList = new(matchCount, matchCount);
        using NativeMemoryListRef<(long Offset, long Length)> boundsList = new(matchCount, matchCount);
        Span<int> srcs = srcsList.AsSpan();
        Span<(long Offset, long Length)> subBounds = boundsList.AsSpan();

        int active = 0;
        for (int j = 0; j < matchCount; j++)
        {
            Bound sb = subTagBounds[j * perSourceStride + subTagIdx];
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
            // Walk the source bytes once for the bloom — the cursor loop below doesn't run.
            AddStorageTrieKeysToBloom<WholeReadSessionReader, NoOpPin>(in r, new Bound(subBounds[0].Offset, subBounds[0].Length), addrKey, bloom);
            return;
        }

        // Multi-source: streaming N-way merge into a PackedArray driven by the shared
        // loser-tree cursor. CopyCurrentLogicalKey returns lex/BE bytes regardless of the
        // source PackedArray's storage layout, so cross-source min selection on cached
        // keys works at innerKeySize ∈ {2,4,8} BE-stored or auto-LE-stored alike.
        using ArrayPoolList<HsstEnumerator> innerEnumsList = new(active, active);
        using NativeMemoryListRef<bool> innerHasMoreList = new(active, active);
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
                bloom.Add(addrKey ^ PersistedSnapshotBloomBuilder.StatePathKey(cursor.MinKey));
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
    /// N-way metadata merge: from_block/from_hash from oldest, to_block/to_hash/version from
    /// newest. Injects noderefs=[0x01]. The merged ref_ids value is produced by an N-way
    /// streaming union over each source's already-sorted little-endian ushort byte span —
    /// no <c>SortedSet&lt;ushort&gt;</c> or <c>ushort[]</c> allocation along the way.
    /// Emits all keys in sorted ASCII order so the inner BTree builder accepts them in
    /// order.
    /// </summary>
    private static void NWayMetadataMerge<TWriter, TReader, TPin>(
        ReadOnlySpan<(IntPtr Ptr, long Len)> views, ref TWriter writer) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        WholeReadSessionReader oldestReader = Reader(views[0]);
        WholeReadSessionReader newestReader = Reader(views[n - 1]);

        // Walk metadata fields directly through the long-aware readers. Each field
        // gets a narrow PinBuffer so the resulting Span is just the field bytes —
        // no wide pin of the entire metadata blob.
        HsstReader<WholeReadSessionReader, NoOpPin> oldestRoot = new(in oldestReader, new Bound(0, oldestReader.Length));
        oldestRoot.TrySeek(PersistedSnapshotTags.MetadataTag, out Bound oldestMetaScope);
        HsstReader<WholeReadSessionReader, NoOpPin> newestRoot = new(in newestReader, new Bound(0, newestReader.Length));
        newestRoot.TrySeek(PersistedSnapshotTags.MetadataTag, out Bound newestMetaScope);

        Bound fb = SeekField(in oldestReader, oldestMetaScope, PersistedSnapshotTags.MetadataFromBlockKey);
        Bound fh = SeekField(in oldestReader, oldestMetaScope, PersistedSnapshotTags.MetadataFromHashKey);
        Bound tb = SeekField(in newestReader, newestMetaScope, PersistedSnapshotTags.MetadataToBlockKey);
        Bound th = SeekField(in newestReader, newestMetaScope, PersistedSnapshotTags.MetadataToHashKey);
        Bound vb = SeekField(in newestReader, newestMetaScope, PersistedSnapshotTags.MetadataVersionKey);

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

        // N-way streaming union of source ref_ids byte spans. Each source's value at
        // MetadataRefIdsKey is already a sorted little-endian ushort sequence (the write
        // path iterates a SortedSet<ushort>); cross-source duplicates are dropped by
        // advancing every cursor whose current ushort matches the round's minimum.
        //
        // First pass: discover each source's ref_ids byte range. sourceStarts[i] is the
        // byte offset into the concatenation buffer where source i's slice begins;
        // sourceStarts[n] is the total byte count (upper bound on merged output).
        // sourceOrigins[i] is the absolute offset within the source view, fed to TryRead.
        Span<int> sourceStarts = stackalloc int[n + 1];
        Span<long> sourceOrigins = stackalloc long[n];
        int totalRefIdsBytes = 0;
        for (int i = 0; i < n; i++)
        {
            sourceStarts[i] = totalRefIdsBytes;
            WholeReadSessionReader r = Reader(views[i]);
            HsstReader<WholeReadSessionReader, NoOpPin> root = new(in r, new Bound(0, r.Length));
            if (!root.TrySeek(PersistedSnapshotTags.MetadataTag, out Bound metaScope)) continue;
            HsstReader<WholeReadSessionReader, NoOpPin> metaHsst = new(in r, metaScope);
            if (!metaHsst.TrySeek(PersistedSnapshotTags.MetadataRefIdsKey, out Bound rb)
                || rb.Length == 0 || rb.Length % 2 != 0) continue;
            sourceOrigins[i] = rb.Offset;
            totalRefIdsBytes = checked(totalRefIdsBytes + (int)rb.Length);
        }
        sourceStarts[n] = totalRefIdsBytes;

        // Pull every source's ref_ids bytes into one contiguous buffer (sourceBytes), then
        // merge into mergedRefIds. Both buffers share the same upper bound, so they're
        // sized to totalRefIdsBytes. NativeMemoryListRef — heap-rented buffer — sidesteps
        // the >2 GiB stackalloc theoretical risk and matches the working-buffer pattern
        // used by the other merge helpers in this file. In practice totalRefIdsBytes is
        // ~tens of bytes.
        using NativeMemoryListRef<byte> sourceBytesBuf = new(totalRefIdsBytes, totalRefIdsBytes);
        using NativeMemoryListRef<byte> mergedRefIdsBuf = new(totalRefIdsBytes, totalRefIdsBytes);
        Span<byte> sourceBytes = sourceBytesBuf.AsSpan();
        Span<byte> mergedRefIds = mergedRefIdsBuf.AsSpan();
        for (int i = 0; i < n; i++)
        {
            int start = sourceStarts[i];
            int len = sourceStarts[i + 1] - start;
            if (len == 0) continue;
            WholeReadSessionReader r = Reader(views[i]);
            r.TryRead(sourceOrigins[i], sourceBytes.Slice(start, len));
        }

        Span<int> cursor = stackalloc int[n];
        for (int i = 0; i < n; i++) cursor[i] = sourceStarts[i];

        int writeCursor = 0;
        while (true)
        {
            int minSource = -1;
            ushort minId = 0;
            for (int i = 0; i < n; i++)
            {
                if (cursor[i] >= sourceStarts[i + 1]) continue;
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(sourceBytes.Slice(cursor[i], 2));
                if (minSource < 0 || id < minId)
                {
                    minSource = i;
                    minId = id;
                }
            }
            if (minSource < 0) break;

            BinaryPrimitives.WriteUInt16LittleEndian(mergedRefIds.Slice(writeCursor, 2), minId);
            writeCursor += 2;

            // Advance every cursor whose current ushort == minId (cross-source dedupe).
            for (int i = 0; i < n; i++)
            {
                if (cursor[i] >= sourceStarts[i + 1]) continue;
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(sourceBytes.Slice(cursor[i], 2));
                if (id == minId) cursor[i] += 2;
            }
        }

        using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, PersistedSnapshotTags.MetadataKeyLength);

        // Emit all keys in sorted ASCII order. NUL-padding to 10 bytes preserves the
        // original ASCII sort order:
        // "from_block" < "from_hash\0" < "noderefs\0\0" < "ref_ids\0\0\0" < "to_block\0\0" < "to_hash\0\0\0" < "version\0\0\0"
        builder.Add(PersistedSnapshotTags.MetadataFromBlockKey, fromBlock);
        builder.Add(PersistedSnapshotTags.MetadataFromHashKey, fromHash);
        builder.Add(PersistedSnapshotTags.MetadataNodeRefsKey, PersistedSnapshotTags.MetadataNodeRefsPresentMarker);
        builder.Add(PersistedSnapshotTags.MetadataRefIdsKey, mergedRefIds[..writeCursor]);
        builder.Add(PersistedSnapshotTags.MetadataToBlockKey, toBlock);
        builder.Add(PersistedSnapshotTags.MetadataToHashKey, toHash);
        builder.Add(PersistedSnapshotTags.MetadataVersionKey, version);

        builder.Build();
    }

    /// <summary>
    /// Walk the outer 30-byte slot-prefix HSST at <paramref name="slotScope"/> and,
    /// for every outer entry, walk the inner 2-byte suffix HSST nested in its value
    /// to compose the full 32-byte slot key. Adds one bloom entry per slot. Used by
    /// the matchCount==1 / slotSourceCount==1 byte-copy fast paths, called against
    /// a reader opened on the destination writer's just-written bytes.
    /// </summary>
    private static void AddSlotKeysToBloom<TReader, TPin>(
        scoped in TReader reader, Bound slotScope, ulong addrKey, BloomFilter bloom)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        Span<byte> slotKey = stackalloc byte[32];
        HsstEnumerator<TReader, TPin> outerEnum = new(in reader, slotScope);
        while (outerEnum.MoveNext(in reader))
        {
            outerEnum.CopyCurrentLogicalKey(in reader, slotKey[..30]);
            Bound innerScope = outerEnum.CurrentValue;
            // The outer entry's value is a keys-first TwoByteSlotValue / -Large sub-slot blob.
            HsstEnumerator<TReader, TPin> innerEnum = HsstEnumerator<TReader, TPin>.CreateTwoByteSlot(in reader, innerScope);
            while (innerEnum.MoveNext(in reader))
            {
                innerEnum.CopyCurrentLogicalKey(in reader, slotKey.Slice(30, 2));
                bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrKey, slotKey));
            }
            innerEnum.Dispose();
        }
        outerEnum.Dispose();
    }

    /// <summary>
    /// Walk a storage-trie sub-tag HSST (top / compact / fallback — keys are 4 / 8 /
    /// 33 bytes respectively) and add <c>StorageNodeKey(addressHash, path)</c> to
    /// <paramref name="bloom"/> for each entry. Mirrors <see cref="AddSlotKeysToBloom"/>
    /// for the byte-copy fast paths in <see cref="MergeStorageTrieSubTag"/> /
    /// <see cref="NWayMergePerAddressColumn"/> where the sub-tag bytes are copied
    /// verbatim and the cursor loop does not run.
    /// </summary>
    private static void AddStorageTrieKeysToBloom<TReader, TPin>(
        scoped in TReader reader, Bound subTagScope, ulong addrKey, BloomFilter bloom)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        Span<byte> keyBuf = stackalloc byte[33];
        HsstEnumerator<TReader, TPin> e = new(in reader, subTagScope);
        while (e.MoveNext(in reader))
        {
            keyBuf.Clear();
            int keyLen = checked((int)e.CurrentKeyLength);
            e.CopyCurrentLogicalKey(in reader, keyBuf[..keyLen]);
            bloom.Add(addrKey ^ PersistedSnapshotBloomBuilder.StatePathKey(keyBuf[..keyLen]));
        }
        e.Dispose();
    }
}
