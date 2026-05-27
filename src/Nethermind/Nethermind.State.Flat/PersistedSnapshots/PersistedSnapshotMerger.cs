// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using HsstEnumerator = Nethermind.State.Flat.Hsst.HsstEnumerator<Nethermind.State.Flat.PersistedSnapshots.Storage.WholeReadSessionReader, Nethermind.State.Flat.Hsst.NoOpPin>;
using Nethermind.State.Flat.Hsst.BTree;
using Nethermind.State.Flat.Hsst.PackedArray;
using Nethermind.State.Flat.Hsst.DenseByteIndex;
using Nethermind.State.Flat.Hsst.TwoByteSlot;

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
    /// <summary>
    /// One source for <see cref="NWayMergeCursor{TReader,TPin,TSource}"/>: the pre-positioned
    /// HSST enumerator plus the <see cref="WholeReadSessionView"/> needed to recreate a fresh
    /// <see cref="WholeReadSessionReader"/> each time the cursor advances. Built once per
    /// cursor slot at merge setup; the cursor copies it by value into its sources span but
    /// every copy shares the same heap-allocated enumerator variant, so iteration state is
    /// preserved.
    /// </summary>
    private readonly struct WholeReadSessionMergeSource(
        HsstEnumerator enumerator, WholeReadSessionView view)
        : IHsstMergeSource<WholeReadSessionReader, NoOpPin>
    {
        public HsstEnumerator GetEnumerator() => enumerator;
        public WholeReadSessionReader CreateReader() => view.CreateReader();
        public void Dispose() => enumerator.Dispose();

        /// <summary>Return a fresh source backed by the same view but driven by
        /// <paramref name="newEnumerator"/>. Used by nested-merge helpers that re-seed a
        /// source at a sub-tag bound without having to plumb the underlying view through
        /// their parameter lists.</summary>
        public WholeReadSessionMergeSource WithEnumerator(HsstEnumerator newEnumerator)
            => new(newEnumerator, view);
    }

    /// <summary>
    /// Constructs a fresh <see cref="HsstEnumerator"/> for <see cref="MapCursorSource{TFactory}"/>.
    /// Stateless struct implementations dispatch over the two HSST layout entry points
    /// (tail-byte <see cref="IndexType"/> vs. front-byte two-byte-slot).
    /// </summary>
    private interface IHsstEnumeratorFactory
    {
        HsstEnumerator Create(scoped in WholeReadSessionReader reader, Bound bound);
    }

    /// <summary>Tail-byte dispatch: <c>new HsstEnumerator(in reader, bound)</c> reads the
    /// trailing <see cref="IndexType"/> byte to pick PackedArray / BTree / BTreeKeyFirst.</summary>
    private readonly struct TailDispatchEnumeratorFactory : IHsstEnumeratorFactory
    {
        public HsstEnumerator Create(scoped in WholeReadSessionReader reader, Bound bound)
            => new(in reader, bound);
    }

    /// <summary>Front-byte dispatch for the keys-first two-byte-slot variants, whose
    /// <see cref="IndexType"/> byte sits at byte 0 of the scope rather than the tail.
    /// Forwards to <see cref="HsstEnumerator.CreateTwoByteSlot"/>.</summary>
    private readonly struct TwoByteSlotEnumeratorFactory : IHsstEnumeratorFactory
    {
        public HsstEnumerator Create(scoped in WholeReadSessionReader reader, Bound bound)
            => HsstEnumerator.CreateTwoByteSlot(in reader, bound);
    }

    /// <summary>
    /// Re-seeds <paramref name="indices"/>.Length cursor sources by cloning entries of
    /// <paramref name="outerSources"/> (selected via <paramref name="indices"/>) at the
    /// matching <paramref name="innerBounds"/>, writing the results into
    /// <paramref name="result"/>. Each clone shares the original source's
    /// <c>WholeReadSessionView</c> (so <c>CreateReader</c> stays cheap) and gets a fresh
    /// <see cref="HsstEnumerator"/> built by <typeparamref name="TFactory"/> over the
    /// per-source inner bound. Used by every nested merge that descends from an outer
    /// column into a sub-tag scope.
    /// </summary>
    /// <remarks>
    /// <paramref name="indices"/>, <paramref name="innerBounds"/>, and
    /// <paramref name="result"/> must all have the same length. Disposal of
    /// <paramref name="result"/>'s entries is the caller's responsibility — one
    /// <c>Dispose()</c> per entry once the inner merge finishes; the underlying view
    /// stays open for further outer iteration.
    /// </remarks>
    private static void MapCursorSource<TFactory>(
        ReadOnlySpan<WholeReadSessionMergeSource> outerSources,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<Bound> innerBounds,
        Span<WholeReadSessionMergeSource> result,
        TFactory factory = default)
        where TFactory : struct, IHsstEnumeratorFactory
    {
        for (int j = 0; j < indices.Length; j++)
        {
            WholeReadSessionMergeSource outer = outerSources[indices[j]];
            WholeReadSessionReader reader = outer.CreateReader();
            result[j] = outer.WithEnumerator(factory.Create(in reader, innerBounds[j]));
        }
    }

    /// <summary>Seed every cursor slot in <paramref name="sources"/> at the column-tag's
    /// bound for the matching <paramref name="views"/> entry. Each source opens a reader,
    /// seeks the column tag in the root HSST, and constructs an enumerator over that bound
    /// (empty bound for sources that don't carry the tag — the loser tree treats them as
    /// exhausted on first MoveNext). Shared by every column-merge helper.</summary>
    private static void SeedSourcesAtColumn(
        ReadOnlySpan<WholeReadSessionView> views, byte[] tag,
        Span<WholeReadSessionMergeSource> sources)
    {
        for (int i = 0; i < views.Length; i++)
        {
            WholeReadSessionReader r = views[i].CreateReader();
            HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
            Bound cb = hsst.TrySeek(tag, out Bound cbOut) ? cbOut : default;
            sources[i] = new(new HsstEnumerator(in r, cb), views[i]);
        }
    }

    /// <summary>For each matching source in <paramref name="cursor"/>'s <c>MatchingSources</c>,
    /// captures the per-source per-address bound from the cursor's current value AND resolves
    /// the per-source sub-tag bounds via <see cref="HsstDenseByteIndexReader.TryResolveAll"/>.
    /// Shared by both BTree value-mergers (per-address column 0x01 with
    /// <c>PerAddrSubTagCount</c> sub-tags, storage-trie column 0x05 with
    /// <c>StorageTrieSubTagCount</c> sub-tags). Caller allocates the output spans sized
    /// <c>matchCount</c> and <c>matchCount * subTagCount</c> respectively.</summary>
    private static void ResolvePerAddrAndSubTagBounds(
        scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> cursor,
        Span<Bound> perAddrBounds, Span<Bound> subTagBounds, int subTagCount)
    {
        ReadOnlySpan<int> matchingSources = cursor.MatchingSources;
        Span<WholeReadSessionMergeSource> sources = cursor.Sources;
        for (int j = 0; j < matchingSources.Length; j++)
        {
            perAddrBounds[j] = cursor.ValueAt(matchingSources[j]);
            WholeReadSessionReader r = sources[matchingSources[j]].CreateReader();
            HsstDenseByteIndexReader.TryResolveAll<WholeReadSessionReader, NoOpPin>(
                in r, perAddrBounds[j],
                subTagBounds.Slice(j * subTagCount, subTagCount));
        }
    }

    /// <summary>Per-key bloom callback for state-trie merges: adds
    /// <c>StatePathKey(minKey)</c> to <paramref name="bloom"/>.</summary>
    private readonly struct StatePathBloomCallback(BloomFilter bloom)
        : IHsstPackedArrayMergeCallback
    {
        public void OnKey(scoped ReadOnlySpan<byte> key)
            => bloom.Add(PersistedSnapshotBloomBuilder.StatePathKey(key));
    }

    /// <summary>Per-key bloom callback for storage-trie sub-tag merges: adds
    /// <c>addrKey ^ StatePathKey(minKey)</c> to <paramref name="bloom"/>, mixing the
    /// per-addressHash key prefix so colliding TreePath keys in different addresses don't
    /// alias in the bloom.</summary>
    private readonly struct AddrXorStatePathBloomCallback(BloomFilter bloom, ulong addrKey)
        : IHsstPackedArrayMergeCallback
    {
        public void OnKey(scoped ReadOnlySpan<byte> key)
            => bloom.Add(addrKey ^ PersistedSnapshotBloomBuilder.StatePathKey(key));
    }

    /// <summary>BTree value merger for the per-address column (tag 0x01). On every emitted
    /// outer key adds <c>addrKey</c> to the bloom. On a fast-copied source value walks the
    /// source's <c>SlotSubTag</c> for per-slot bloom adds. On a multi-source (or oversized
    /// single-source) rebuild resolves each contributing source's per-address bounds and
    /// per-source sub-tag bounds, then delegates to
    /// <see cref="NWayMergePerAddressHsst{TWriter,TReader,TPin}"/> to stream the merged
    /// DenseByteIndex through the outer builder's value writer.</summary>
    /// <remarks>Cursor-side reader/pin are pinned to (<see cref="WholeReadSessionReader"/>,
    /// <see cref="NoOpPin"/>) because the merge always reads from open snapshot mmaps; the
    /// three generic parameters are the WRITER-side trio threaded through to
    /// <see cref="NWayMergePerAddressHsst{TWriter,TReader,TPin}"/>. Per-source reader
    /// factories come via the cursor (<c>cursor.CreateMinReader</c>, <c>cursor.Sources</c>).
    /// The shared <see cref="HsstBTreeBuilderBuffers"/> arena (re-used across every emitted
    /// address) is held via <see cref="HsstBTreeBuilderBuffersContainer"/> — a class handle
    /// that hides the ref-to-ref-struct workaround.</remarks>
    private readonly struct PerAddressColumnValueMerger<TWriter, TReader, TPin>(
        BloomFilter bloom, HsstBTreeBuilderBuffersContainer slotPrefixBuffers)
        : IHsstBTreeValueMerger<TWriter, WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource>
        where TWriter : IByteBufferWriterWithReader<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        public void OnKey(scoped ReadOnlySpan<byte> key)
            => bloom.Add(MemoryMarshal.Read<ulong>(key));

        public void OnFastCopy(scoped ReadOnlySpan<byte> key,
            scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> cursor)
        {
            Bound vb = cursor.MinValue;
            ulong addrKey = MemoryMarshal.Read<ulong>(key);
            WholeReadSessionReader srcReader = cursor.CreateMinReader();
            HsstReader<WholeReadSessionReader, NoOpPin> outer = new(in srcReader, vb);
            if (outer.TrySeek(PersistedSnapshotTags.SlotSubTag, out Bound slotBound))
                AddSlotKeysToBloom<WholeReadSessionReader, NoOpPin>(in srcReader, slotBound, addrKey, bloom);
        }

        public void MergeValues(ref TWriter writer, scoped ReadOnlySpan<byte> key,
            scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> cursor)
        {
            ulong addrKey = MemoryMarshal.Read<ulong>(key);
            ReadOnlySpan<int> matchingSources = cursor.MatchingSources;
            int matchCount = matchingSources.Length;
            const int SubTagCount = PersistedSnapshotTags.PerAddrSubTagCount;

            using NativeMemoryListRef<Bound> perAddrBoundsList = new(matchCount, matchCount);
            using NativeMemoryListRef<Bound> subTagBoundsList = new(matchCount * SubTagCount, matchCount * SubTagCount);
            Span<Bound> perAddrBounds = perAddrBoundsList.AsSpan();
            Span<Bound> subTagBounds = subTagBoundsList.AsSpan();
            ResolvePerAddrAndSubTagBounds(ref cursor, perAddrBounds, subTagBounds, SubTagCount);

            NWayMergePerAddressHsst<TWriter, TReader, TPin>(
                matchingSources, matchCount, cursor.Sources,
                ref writer, ref slotPrefixBuffers.Buffers,
                subTagBounds,
                bloom, addrKey);
        }
    }

    /// <summary>BTree value merger for the storage-trie column (tag 0x05). No per-outer-key
    /// bloom add (only slot keys are bloomed). On a fast-copied source value walks the
    /// three storage-trie sub-tags (top / compact / fallback) for per-node bloom adds. On a
    /// multi-source (or oversized single-source) rebuild assembles a fresh per-addressHash
    /// DenseByteIndex with the three sub-tag merges emitted in descending tag order via
    /// <see cref="MergeStorageTrieSubTag{TWriter,TReader,TPin}"/>.</summary>
    /// <remarks>Cursor-side reader/pin are pinned to (<see cref="WholeReadSessionReader"/>,
    /// <see cref="NoOpPin"/>); the three generic parameters are the WRITER-side trio
    /// threaded through to <see cref="MergeStorageTrieSubTag{TWriter,TReader,TPin}"/>.
    /// Per-source reader factories come via the cursor (<c>cursor.CreateMinReader</c>,
    /// <c>cursor.Sources</c>); no <c>_views</c> field is needed.</remarks>
    private readonly struct StorageTrieColumnValueMerger<TWriter, TReader, TPin>(BloomFilter bloom)
        : IHsstBTreeValueMerger<TWriter, WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource>
        where TWriter : IByteBufferWriterWithReader<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        public void OnKey(scoped ReadOnlySpan<byte> key) { }

        public void OnFastCopy(scoped ReadOnlySpan<byte> key,
            scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> cursor)
        {
            Bound vb = cursor.MinValue;
            ulong addrKey = MemoryMarshal.Read<ulong>(key);
            WholeReadSessionReader srcReader = cursor.CreateMinReader();
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
        }

        public void MergeValues(ref TWriter writer, scoped ReadOnlySpan<byte> key,
            scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> cursor)
        {
            ulong addrKey = MemoryMarshal.Read<ulong>(key);
            ReadOnlySpan<int> matchingSources = cursor.MatchingSources;
            int matchCount = matchingSources.Length;
            const int SubTagCount = PersistedSnapshotTags.StorageTrieSubTagCount;

            using NativeMemoryListRef<Bound> perAddrBoundsList = new(matchCount, matchCount);
            using NativeMemoryListRef<Bound> subTagBoundsList = new(matchCount * SubTagCount, matchCount * SubTagCount);
            Span<Bound> perAddrBounds = perAddrBoundsList.AsSpan();
            Span<Bound> subTagBounds = subTagBoundsList.AsSpan();
            ResolvePerAddrAndSubTagBounds(ref cursor, perAddrBounds, subTagBounds, SubTagCount);
            Span<WholeReadSessionMergeSource> sources = cursor.Sources;

            HsstDenseByteIndexBuilder<TWriter> perAddrBuilder = new(ref writer);
            try
            {
                // Emit descending 0x02 (fallback) → 0x01 (compact) → 0x00 (top).
                MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, sources, subTagBounds,
                    ref perAddrBuilder, PersistedSnapshotTags.StorageFallbackSubTag,
                    subTagIdx: PersistedSnapshotTags.StorageFallbackSubTag[0], innerKeySize: 33, perSourceStride: SubTagCount,
                    bloom, addrKey);
                MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, sources, subTagBounds,
                    ref perAddrBuilder, PersistedSnapshotTags.StorageCompactSubTag,
                    subTagIdx: PersistedSnapshotTags.StorageCompactSubTag[0], innerKeySize: 8, perSourceStride: SubTagCount,
                    bloom, addrKey);
                MergeStorageTrieSubTag<TWriter, TReader, TPin>(matchingSources, matchCount, sources, subTagBounds,
                    ref perAddrBuilder, PersistedSnapshotTags.StorageTopSubTag,
                    subTagIdx: PersistedSnapshotTags.StorageTopSubTag[0], innerKeySize: 4, perSourceStride: SubTagCount,
                    bloom, addrKey);
                perAddrBuilder.Build();
            }
            finally
            {
                perAddrBuilder.Dispose();
            }
        }
    }

    /// <summary>
    /// Per-call scratch for <see cref="SlotPrefixValueMerger"/>: holds the buffers
    /// reused across outer keys of a single
    /// <see cref="NWayNestedStreamingSlotMerge{TWriter,TReader,TPin}"/> invocation.
    /// One instance per per-address slot-prefix merge; held by reference on the
    /// value-merger struct so callbacks can reach it across method boundaries.
    /// </summary>
    private sealed class SlotPrefixValueMergerScratch : IDisposable
    {
        public readonly byte[] SlotKeyBuf;
        public readonly Bound[] InnerBoundsScratch;
        public readonly ArrayPoolList<WholeReadSessionMergeSource> InnerSources;
        public readonly ArrayPoolList<byte> ScratchValues;
        public readonly ArrayPoolList<byte> ScratchKeys;
        public readonly ArrayPoolList<int> ScratchLens;

        public SlotPrefixValueMergerScratch(int n)
        {
            const int InnerKeyLen = 2;
            SlotKeyBuf = new byte[32];
            InnerBoundsScratch = new Bound[n];
            InnerSources = new ArrayPoolList<WholeReadSessionMergeSource>(n, n);
            ScratchValues = new ArrayPoolList<byte>(512);
            ScratchKeys = new ArrayPoolList<byte>(Math.Max(1, n) * InnerKeyLen);
            ScratchLens = new ArrayPoolList<int>(Math.Max(1, n));
        }

        public void Dispose()
        {
            InnerSources.Dispose();
            ScratchValues.Dispose();
            ScratchKeys.Dispose();
            ScratchLens.Dispose();
        }
    }

    /// <summary>
    /// BTree value merger for the per-address slot-prefix column. Outer is a keyFirst
    /// 30-byte BTree of slot prefixes; each outer entry's value is a keys-first
    /// TwoByteSlotValue / TwoByteSlotValueLarge HSST of the remaining 2-byte slot
    /// suffixes. Drives the inner 2-byte merge from the matched outer sources,
    /// buffers merged keys/values into the scratch, picks the inner format by total
    /// payload size, and emits the chosen blob into the staging writer that
    /// <see cref="HsstBTreeMerger.NWayMergeKeyFirst"/> hands in.
    /// </summary>
    /// <remarks>
    /// TWriter is fixed to <see cref="PooledByteBufferWriter.Writer"/> because the
    /// keyFirst BTree builder needs the value length up front, so
    /// <see cref="HsstBTreeMerger.NWayMergeKeyFirst"/> stages each value through an
    /// internal <see cref="PooledByteBufferWriter"/> and then calls
    /// <c>builder.Add(key, stagedSpan)</c>. The scratch lives on a class so this
    /// struct can hold it by reference across the
    /// <see cref="IHsstBTreeValueMerger{TWriter,TReader,TPin,TSource}"/> callbacks.
    /// </remarks>
    private readonly struct SlotPrefixValueMerger(
        BloomFilter bloom, ulong addrBloomKey, SlotPrefixValueMergerScratch scratch)
        : IHsstBTreeValueMerger<PooledByteBufferWriter.Writer, WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource>
    {
        private const int OuterKeyLen = 30;
        private const int InnerKeyLen = 2;

        public void OnKey(scoped ReadOnlySpan<byte> key) { }

        public void OnFastCopy(scoped ReadOnlySpan<byte> key,
            scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> cursor)
        {
            Bound vb = cursor.MinValue;
            WholeReadSessionReader srcReader = cursor.CreateMinReader();
            Span<byte> slotKeyBuf = scratch.SlotKeyBuf;
            key.CopyTo(slotKeyBuf[..OuterKeyLen]);
            HsstEnumerator suffixEnum = HsstEnumerator.CreateTwoByteSlot(in srcReader, vb);
            while (suffixEnum.MoveNext(in srcReader))
            {
                suffixEnum.CopyCurrentLogicalKey(in srcReader, slotKeyBuf.Slice(OuterKeyLen, InnerKeyLen));
                bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, slotKeyBuf));
            }
            suffixEnum.Dispose();
        }

        public void MergeValues(ref PooledByteBufferWriter.Writer writer, scoped ReadOnlySpan<byte> key,
            scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> cursor)
        {
            int matchCount = cursor.MatchCount;
            ReadOnlySpan<int> matchingSources = cursor.MatchingSources;
            Span<byte> slotKeyBuf = scratch.SlotKeyBuf;
            key.CopyTo(slotKeyBuf[..OuterKeyLen]);

            using LoserTreeState innerState = new(matchCount, InnerKeyLen);
            Span<Bound> innerBounds = scratch.InnerBoundsScratch.AsSpan(0, matchCount);
            for (int k = 0; k < matchCount; k++)
                innerBounds[k] = cursor.ValueAt(matchingSources[k]);
            Span<WholeReadSessionMergeSource> innerSources = scratch.InnerSources.AsSpan()[..matchCount];
            MapCursorSource<TwoByteSlotEnumeratorFactory>(
                cursor.Sources, matchingSources, innerBounds, innerSources);
            try
            {
                NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> innerCursor = new(
                    innerSources, innerState, InnerKeyLen);

                ArrayPoolList<byte> scratchValues = scratch.ScratchValues;
                ArrayPoolList<byte> scratchKeys = scratch.ScratchKeys;
                ArrayPoolList<int> scratchLens = scratch.ScratchLens;
                scratchValues.Clear();
                scratchKeys.Clear();
                scratchLens.Clear();

                while (innerCursor.MoveNext())
                {
                    Bound vb = innerCursor.MinValue;
                    using NoOpPin valPin = innerCursor.CreateMinReader().PinBuffer(vb.Offset, vb.Length);
                    ReadOnlySpan<byte> innerKey = innerCursor.MinKey;
                    innerKey.CopyTo(slotKeyBuf.Slice(OuterKeyLen, InnerKeyLen));
                    bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, slotKeyBuf));
                    scratchValues.AddRange(valPin.Buffer);
                    scratchKeys.AddRange(innerKey);
                    scratchLens.Add((int)vb.Length);
                    innerCursor.AdvanceMatching();
                }

                ReadOnlySpan<byte> mergedValues = scratchValues.AsSpan();
                ReadOnlySpan<byte> mergedKeys = scratchKeys.AsSpan();
                ReadOnlySpan<int> mergedLens = scratchLens.AsSpan();
                if (HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer>.FitsInOffsetWidth(mergedValues.Length))
                {
                    using HsstTwoByteSlotValueBuilder<PooledByteBufferWriter.Writer> innerBuilder = new(ref writer);
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
                    using HsstTwoByteSlotValueLargeBuilder<PooledByteBufferWriter.Writer> innerBuilder = new(ref writer);
                    int valOff = 0;
                    for (int i = 0; i < mergedLens.Length; i++)
                    {
                        innerBuilder.Add(mergedKeys.Slice(i * InnerKeyLen, InnerKeyLen), mergedValues.Slice(valOff, mergedLens[i]));
                        valOff += mergedLens[i];
                    }
                    innerBuilder.Build();
                }
            }
            finally
            {
                for (int k = 0; k < matchCount; k++) innerSources[k].Dispose();
            }
        }
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
        ReadOnlySpan<WholeReadSessionView> views, ref TWriter writer,
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
        ReadOnlySpan<WholeReadSessionView> views, byte[] tag, ref TWriter writer,
        int keySize, BloomFilter bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        // Cache each source's current logical key once per MoveNext so the O(log N) cursor
        // and O(N) match-detection scans don't redo CopyCurrentLogicalKey per output key.
        int keyStride = Math.Max(1, keySize);
        using LoserTreeState state = new(n, keyStride);
        using ArrayPoolList<WholeReadSessionMergeSource> sourcesList = new(n, n);
        Span<WholeReadSessionMergeSource> sources = sourcesList.AsSpan();

        try
        {
            SeedSourcesAtColumn(views, tag, sources);
            NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> cursor = new(
                sources, state, keySize);

            HsstPackedArrayMerger.NWayMerge<TWriter, WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, StatePathBloomCallback>(
                ref writer, NodeRef.Size, ref cursor, new StatePathBloomCallback(bloom));
        }
        finally
        {
            for (int i = 0; i < n; i++) sources[i].Dispose();
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
        ReadOnlySpan<WholeReadSessionView> views, byte[] tag, ref TWriter writer, BloomFilter bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        // Cache each source's current 20-byte Address key (stride 32 with room).
        const int KeyStride = 32;
        const int AddrKeyLen = PersistedSnapshotTags.AddressKeyLength;
        using LoserTreeState state = new(n, KeyStride);
        using ArrayPoolList<WholeReadSessionMergeSource> sourcesList = new(n, n);
        Span<WholeReadSessionMergeSource> sources = sourcesList.AsSpan();

        // Reusable work buffers for the per-address slot prefix/suffix HSST builders.
        // The container is a class so the value-merger can hold it as a regular field; the
        // contained buffers live across every merged address — the prefix builder is created
        // once per address and the suffix builder once per prefix group per address, so
        // amortising the rentals matters.
        using HsstBTreeBuilderBuffersContainer slotPrefixBuffers = new();

        try
        {
            SeedSourcesAtColumn(views, tag, sources);
            NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> cursor = new(
                sources, state, AddrKeyLen);

            PerAddressColumnValueMerger<TWriter, TReader, TPin> valueMerger =
                new(bloom, slotPrefixBuffers);
            HsstBTreeMerger.NWayMerge<TWriter, TReader, TPin,
                WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource,
                PerAddressColumnValueMerger<TWriter, TReader, TPin>>(
                ref writer, AddrKeyLen, ref cursor, valueMerger);
        }
        finally
        {
            for (int i = 0; i < n; i++) sources[i].Dispose();
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
        ReadOnlySpan<WholeReadSessionView> views, byte[] tag, ref TWriter writer, BloomFilter bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        const int KeyStride = 32;
        const int AddrKeyLen = PersistedSnapshotTags.AddressHashPrefixLength;
        using LoserTreeState state = new(n, KeyStride);
        using ArrayPoolList<WholeReadSessionMergeSource> sourcesList = new(n, n);
        Span<WholeReadSessionMergeSource> sources = sourcesList.AsSpan();

        try
        {
            SeedSourcesAtColumn(views, tag, sources);
            NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> cursor = new(
                sources, state, AddrKeyLen);

            StorageTrieColumnValueMerger<TWriter, TReader, TPin> valueMerger = new(bloom);
            HsstBTreeMerger.NWayMerge<TWriter, TReader, TPin,
                WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource,
                StorageTrieColumnValueMerger<TWriter, TReader, TPin>>(
                ref writer, AddrKeyLen, ref cursor, valueMerger);
        }
        finally
        {
            for (int i = 0; i < n; i++) sources[i].Dispose();
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
        Span<WholeReadSessionMergeSource> outerSources,
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
                WholeReadSessionReader r = outerSources[matchingSources[j]].CreateReader();
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
                using NativeMemoryListRef<Bound> slotBoundsList = new(slotCapacity, slotCapacity);
                Span<int> slotSources = slotSourcesList.AsSpan();
                Span<Bound> slotBounds = slotBoundsList.AsSpan();
                for (int j = slotStart; j < matchCount; j++)
                {
                    Bound slotBound = subTagBounds[j * PersistedSnapshotTags.PerAddrSubTagCount + slotTag];
                    if (slotBound.Length > 0)
                    {
                        slotSources[slotSourceCount] = matchingSources[j];
                        slotBounds[slotSourceCount] = slotBound;
                        slotSourceCount++;
                    }
                }

                if (slotSourceCount > 0)
                {
                    using ArrayPoolList<WholeReadSessionMergeSource> slotMergeSourcesList = new(slotSourceCount, slotSourceCount);
                    Span<WholeReadSessionMergeSource> slotSrcArr = slotMergeSourcesList.AsSpan();
                    try
                    {
                        MapCursorSource<TailDispatchEnumeratorFactory>(
                            outerSources, slotSources[..slotSourceCount], slotBounds[..slotSourceCount], slotSrcArr);

                        ref TWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                        NWayNestedStreamingSlotMerge<TWriter, TReader, TPin>(
                            slotSrcArr, slotSourceCount,
                            ref slotWriter,
                            ref slotPrefixBuffers,
                            bloom, addrBloomKey);
                        perAddrBuilder.FinishValueWrite(PersistedSnapshotTags.SlotSubTag);
                    }
                    finally
                    {
                        for (int j = 0; j < slotSourceCount; j++) slotSrcArr[j].Dispose();
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
                        WholeReadSessionReader r = outerSources[matchingSources[j]].CreateReader();
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
                    WholeReadSessionReader r = outerSources[matchingSources[sdSrcJ]].CreateReader();
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
                    WholeReadSessionReader r = outerSources[matchingSources[j]].CreateReader();
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
        Span<WholeReadSessionMergeSource> outerSources, int n,
        ref TWriter writer,
        scoped ref HsstBTreeBuilderBuffers slotPrefixBuffers,
        BloomFilter bloom, ulong addrBloomKey) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        const int OuterKeyLen = 30;
        const int OuterStride = 32;
        using LoserTreeState outerState = new(n, OuterStride);
        using SlotPrefixValueMergerScratch scratch = new(n);

        NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> outerCursor = new(
            outerSources[..n], outerState, OuterKeyLen);

        HsstBTreeMerger.NWayMergeKeyFirst<
            TWriter, TReader, TPin,
            WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource,
            SlotPrefixValueMerger>(
                ref writer, OuterKeyLen, ref outerCursor,
                new SlotPrefixValueMerger(bloom, addrBloomKey, scratch),
                ref slotPrefixBuffers);
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
        Span<WholeReadSessionMergeSource> outerSources,
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
        using NativeMemoryListRef<Bound> boundsList = new(matchCount, matchCount);
        Span<int> srcs = srcsList.AsSpan();
        Span<Bound> subBounds = boundsList.AsSpan();

        int active = 0;
        for (int j = 0; j < matchCount; j++)
        {
            Bound sb = subTagBounds[j * perSourceStride + subTagIdx];
            if (sb.Length > 0)
            {
                srcs[active] = j;
                subBounds[active] = sb;
                active++;
            }
        }

        if (active == 0) return;

        if (active == 1)
        {
            int j = srcs[0];
            WholeReadSessionReader r = outerSources[matchingSources[j]].CreateReader();
            using NoOpPin pin = r.PinBuffer(subBounds[0].Offset, subBounds[0].Length);
            perAddrBuilder.Add(subTag, pin.Buffer);
            // Walk the source bytes once for the bloom — the cursor loop below doesn't run.
            AddStorageTrieKeysToBloom<WholeReadSessionReader, NoOpPin>(in r, subBounds[0], addrKey, bloom);
            return;
        }

        // Multi-source: streaming N-way merge into a PackedArray driven by the shared
        // loser-tree cursor. CopyCurrentLogicalKey returns lex/BE bytes regardless of the
        // source PackedArray's storage layout, so cross-source min selection on cached
        // keys works at innerKeySize ∈ {2,4,8} BE-stored or auto-LE-stored alike.
        using LoserTreeState state = new(active, innerKeySize);
        using ArrayPoolList<WholeReadSessionMergeSource> sourcesList = new(active, active);
        Span<WholeReadSessionMergeSource> sources = sourcesList.AsSpan();

        try
        {
            Span<int> outerIndices = stackalloc int[active];
            for (int j = 0; j < active; j++) outerIndices[j] = matchingSources[srcs[j]];
            MapCursorSource<TailDispatchEnumeratorFactory>(
                outerSources, outerIndices, subBounds[..active], sources);
            NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource> cursor = new(
                sources, state, innerKeySize);

            ref TWriter subWriter = ref perAddrBuilder.BeginValueWrite();
            HsstPackedArrayMerger.NWayMerge<TWriter, WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, AddrXorStatePathBloomCallback>(
                ref subWriter, NodeRef.Size, ref cursor, new AddrXorStatePathBloomCallback(bloom, addrKey));
            perAddrBuilder.FinishValueWrite(subTag);
        }
        finally
        {
            for (int j = 0; j < active; j++) sources[j].Dispose();
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
        ReadOnlySpan<WholeReadSessionView> views, ref TWriter writer) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        WholeReadSessionReader oldestReader = views[0].CreateReader();
        WholeReadSessionReader newestReader = views[n - 1].CreateReader();

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
            WholeReadSessionReader r = views[i].CreateReader();
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
            WholeReadSessionReader r = views[i].CreateReader();
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

        using HsstBTreeBuilderBuffersContainer buffers = new();
        using HsstBTreeBuilder<TWriter, TReader, TPin> builder = new(ref writer, ref buffers.Buffers, PersistedSnapshotTags.MetadataKeyLength);

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
