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
    private readonly struct WholeReadSessionMergeSource(WholeReadSessionView view, Bound bound)
        : IHsstMergeSource<WholeReadSessionReader, NoOpPin>
    {
        public WholeReadSessionReader CreateReader() => view.CreateReader();
        public Bound Bound => bound;

        /// <summary>Re-seed at a different bound (same view). Used by
        /// <see cref="BuildMergeCursor{TFactory}"/> in nested-merge re-seeds.</summary>
        public WholeReadSessionMergeSource WithBound(Bound newBound) => new(view, newBound);
    }

    /// <summary>Open a fresh reader on <paramref name="view"/>, seek the root HSST for
    /// <paramref name="columnTag"/>, and return its bound (or an empty bound if the tag
    /// is absent — sources at the empty bound are treated as exhausted on first
    /// MoveNext).</summary>
    private static Bound ResolveColumnBound(WholeReadSessionView view, byte[] columnTag)
    {
        WholeReadSessionReader r = view.CreateReader();
        HsstReader<WholeReadSessionReader, NoOpPin> hsst = new(in r, new Bound(0, r.Length));
        return hsst.TrySeek(columnTag, out Bound b) ? b : default;
    }

    /// <summary>Tail-byte dispatch: <c>new HsstEnumerator(in reader, bound)</c> reads the
    /// trailing <see cref="IndexType"/> byte to pick PackedArray / BTree / BTreeKeyFirst.</summary>
    private readonly struct TailDispatchEnumeratorFactory : IHsstEnumeratorFactory<WholeReadSessionReader, NoOpPin>
    {
        public HsstEnumerator Create(scoped in WholeReadSessionReader reader, Bound bound)
            => new(in reader, bound);
    }

    /// <summary>
    /// Re-seeds <paramref name="indices"/>.Length sources by cloning entries of
    /// <paramref name="outerSources"/> at the matching <paramref name="innerBounds"/>,
    /// writing them into <paramref name="sourcesBuf"/>, and returning a cursor over the
    /// result. Each clone shares the original source's <c>WholeReadSessionView</c> with a
    /// rewritten <see cref="Bound"/>; the cursor constructs the per-slot
    /// <see cref="HsstEnumerator"/> via <typeparamref name="TFactory"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="indices"/>, <paramref name="innerBounds"/>,
    /// <paramref name="sourcesBuf"/>, and <paramref name="enumeratorsBuf"/> must each have
    /// at least <paramref name="indices"/>.Length elements.
    /// </remarks>
    private static NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TFactory>
        BuildMergeCursor<TFactory>(
            ReadOnlySpan<WholeReadSessionMergeSource> outerSources,
            ReadOnlySpan<int> indices,
            ReadOnlySpan<Bound> innerBounds,
            Span<WholeReadSessionMergeSource> sourcesBuf,
            Span<HsstEnumerator> enumeratorsBuf,
            LoserTreeState state,
            int keyLen,
            TFactory factory = default)
        where TFactory : struct, IHsstEnumeratorFactory<WholeReadSessionReader, NoOpPin>
    {
        for (int j = 0; j < indices.Length; j++)
            sourcesBuf[j] = outerSources[indices[j]].WithBound(innerBounds[j]);
        return new NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TFactory>(
            sourcesBuf[..indices.Length], enumeratorsBuf[..indices.Length], state, keyLen, factory);
    }

    /// <summary>For each matching source in <paramref name="cursor"/>'s <c>MatchingSources</c>,
    /// captures the per-source per-address bound from the cursor's current value AND resolves
    /// the per-source sub-tag bounds via <see cref="HsstDenseByteIndexReader.TryResolveAll"/>.
    /// Shared by both BTree value-mergers (per-address column 0x01 with
    /// <c>PerAddrSubTagCount</c> sub-tags, storage-trie column 0x05 with
    /// <c>StorageTrieSubTagCount</c> sub-tags). Caller allocates the output spans sized
    /// <c>matchCount</c> and <c>matchCount * subTagCount</c> respectively.</summary>
    private static void ResolvePerAddrAndSubTagBounds(
        scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> cursor,
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

    /// <summary>BTree value merger for the per-address column (tag 0x01). On every emitted
    /// outer key adds <c>addrKey</c> to the bloom. On a fast-copied source value walks the
    /// source's <c>SlotSubTag</c> for per-slot bloom adds. On a multi-source (or oversized
    /// single-source) rebuild resolves each contributing source's per-address bounds and
    /// per-source sub-tag bounds, then streams the merged per-address DenseByteIndex
    /// (sub-tags 0x02 Slots, 0x01 SelfDestruct, 0x00 Account) through the outer builder's
    /// value writer.</summary>
    /// <remarks>Cursor-side reader/pin are pinned to (<see cref="WholeReadSessionReader"/>,
    /// <see cref="NoOpPin"/>) because the merge always reads from open snapshot mmaps; the
    /// three generic parameters are the WRITER-side trio threaded through to the inner
    /// DenseByteIndex builder and the nested slot-prefix merger. Per-source reader factories
    /// come via the cursor (<c>cursor.CreateMinReader</c>, <c>cursor.Sources</c>).
    /// The shared <see cref="HsstBTreeBuilderBuffers"/> arena (re-used across every emitted
    /// address) is held via <see cref="HsstBTreeBuilderBuffersContainer"/> — a class handle
    /// that hides the ref-to-ref-struct workaround.</remarks>
    private readonly struct PerAddressColumnValueMerger<TWriter, TReader, TPin>(
        BloomFilter bloom, HsstBTreeBuilderBuffersContainer slotPrefixBuffers)
        : IHsstBTreeValueMerger<TWriter, WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory>
        where TWriter : IByteBufferWriterWithReader<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        public void OnKey(scoped ReadOnlySpan<byte> key)
            => bloom.Add(MemoryMarshal.Read<ulong>(key));

        public void OnFastCopy(scoped ReadOnlySpan<byte> key,
            scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> cursor)
        {
            Bound vb = cursor.MinValue;
            ulong addrKey = MemoryMarshal.Read<ulong>(key);
            WholeReadSessionReader srcReader = cursor.CreateMinReader();
            HsstReader<WholeReadSessionReader, NoOpPin> outer = new(in srcReader, vb);
            if (outer.TrySeek(PersistedSnapshotTags.SlotSubTag, out Bound slotBound))
                AddSlotKeysToBloom<WholeReadSessionReader, NoOpPin>(in srcReader, slotBound, addrKey, bloom);
        }

        public void MergeValues(ref TWriter writer, scoped ReadOnlySpan<byte> key,
            scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> cursor)
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

            // perAddrBuilder is passed to several helpers by ref, so it can't be a `using`
            // declaration (the compiler refuses ref to using-variables). Manage its disposal
            // with a try/finally instead.
            HsstDenseByteIndexBuilder<TWriter> perAddrBuilder = new(ref writer);
            try
            {
                // Emit descending 0x02 (Slots) → 0x01 (SelfDestruct) → 0x00 (Account) so
                // the per-address DenseByteIndex receives sub-tags in strictly descending order.
                MergeSlots(cursor.Sources, matchingSources, matchCount, subTagBounds, ref perAddrBuilder, addrKey);
                MergeSelfDestruct(cursor.Sources, matchingSources, matchCount, subTagBounds, ref perAddrBuilder);
                MergeAccount(cursor.Sources, matchingSources, matchCount, subTagBounds, ref perAddrBuilder);
                perAddrBuilder.Build();
            }
            finally
            {
                perAddrBuilder.Dispose();
            }
        }

        /// <summary>Sub-tag 0x02: emit the merged slot HSST. Finds the newest destruct
        /// barrier (newest source where SelfDestructSubTag is destructed-marked), then
        /// drives an outer 30-byte slot-prefix keyFirst BTree merge over slot-bearing
        /// sources from <c>max(0, destructBarrier)..matchCount-1</c> via
        /// <see cref="HsstBTreeMerger.NWayMergeKeyFirst"/> with
        /// <see cref="SlotPrefixValueMerger"/> handling the inner 2-byte suffix merge.
        /// We do not byte-copy a single-source slot blob through perAddrBuilder here:
        /// the dense byte index does not page-align its values, so re-emitting through
        /// the inner BTree builder (which does align) keeps the slot HSST on its own
        /// page.</summary>
        private void MergeSlots(
            ReadOnlySpan<WholeReadSessionMergeSource> sources,
            ReadOnlySpan<int> matchingSources, int matchCount,
            ReadOnlySpan<Bound> subTagBounds,
            scoped ref HsstDenseByteIndexBuilder<TWriter> perAddrBuilder,
            ulong addrKey)
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
                WholeReadSessionReader r = sources[matchingSources[j]].CreateReader();
                using NoOpPin sdPin = r.PinBuffer(sdb.Offset, 1);
                if (sdPin.Buffer[0] == PersistedSnapshotTags.SelfDestructDestructedMarkerByte)
                    destructBarrier = j;
            }

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
                const int OuterKeyLen = 30;
                const int OuterStride = 32;
                using LoserTreeState outerState = new(slotSourceCount, OuterStride);
                using SlotPrefixValueMergerScratch scratch = new(slotSourceCount);
                using ArrayPoolList<WholeReadSessionMergeSource> slotPrefixSourcesList = new(slotSourceCount, slotSourceCount);
                using ArrayPoolList<HsstEnumerator> slotPrefixEnumeratorsList = new(slotSourceCount, slotSourceCount);
                Span<WholeReadSessionMergeSource> slotPrefixSources = slotPrefixSourcesList.AsSpan();
                Span<HsstEnumerator> slotPrefixEnumerators = slotPrefixEnumeratorsList.AsSpan();

                NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> outerCursor =
                    BuildMergeCursor(sources, slotSources[..slotSourceCount], slotBounds[..slotSourceCount],
                        slotPrefixSources, slotPrefixEnumerators, outerState, OuterKeyLen,
                        default(TailDispatchEnumeratorFactory));

                ref TWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                HsstBTreeMerger.NWayMergeKeyFirst<
                    TWriter, TReader, TPin,
                    WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory,
                    SlotPrefixValueMerger>(
                        ref slotWriter, OuterKeyLen, ref outerCursor,
                        new SlotPrefixValueMerger(bloom, addrKey, scratch),
                        ref slotPrefixBuffers.Buffers);
                perAddrBuilder.FinishValueWrite(PersistedSnapshotTags.SlotSubTag);
            }
        }

        /// <summary>Sub-tag 0x01: iterate sources 0..M-1, apply TryAdd semantics
        /// (newer=destructed [0x00] wins; newer=new [0x01] keeps the older). Presence is
        /// signalled by length>0; absent entries (gap-filled length 0 under DenseByteIndex)
        /// are ignored. Track the winning bound snapshot-absolute so we can re-pin at the
        /// end without holding a span across iterations.</summary>
        private void MergeSelfDestruct(
            ReadOnlySpan<WholeReadSessionMergeSource> sources,
            ReadOnlySpan<int> matchingSources, int matchCount,
            ReadOnlySpan<Bound> subTagBounds,
            scoped ref HsstDenseByteIndexBuilder<TWriter> perAddrBuilder)
        {
            int sdTag = PersistedSnapshotTags.SelfDestructSubTag[0];
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
                    WholeReadSessionReader r = sources[matchingSources[j]].CreateReader();
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
                WholeReadSessionReader r = sources[matchingSources[sdSrcJ]].CreateReader();
                using NoOpPin sdPin = r.PinBuffer(sdValOff, sdValLen);
                perAddrBuilder.Add(PersistedSnapshotTags.SelfDestructSubTag, sdPin.Buffer);
            }
        }

        /// <summary>Sub-tag 0x00: newest wins. Walk M-1..0, first present (length>0).
        /// Emitted last so the hot Account blob lands adjacent to the DenseByteIndex
        /// Ends[] trailer.</summary>
        private void MergeAccount(
            ReadOnlySpan<WholeReadSessionMergeSource> sources,
            ReadOnlySpan<int> matchingSources, int matchCount,
            ReadOnlySpan<Bound> subTagBounds,
            scoped ref HsstDenseByteIndexBuilder<TWriter> perAddrBuilder)
        {
            int acctTag = PersistedSnapshotTags.AccountSubTag[0];
            for (int j = matchCount - 1; j >= 0; j--)
            {
                Bound ab = subTagBounds[j * PersistedSnapshotTags.PerAddrSubTagCount + acctTag];
                if (ab.Length == 0) continue;
                WholeReadSessionReader r = sources[matchingSources[j]].CreateReader();
                using NoOpPin acctPin = r.PinBuffer(ab.Offset, ab.Length);
                perAddrBuilder.Add(PersistedSnapshotTags.AccountSubTag, acctPin.Buffer);
                break;
            }
        }

        /// <summary>
        /// Walk the outer 30-byte slot-prefix HSST at <paramref name="slotScope"/> and,
        /// for every outer entry, walk the inner 2-byte suffix HSST nested in its value
        /// to compose the full 32-byte slot key. Adds one bloom entry per slot. Used by
        /// the matchCount==1 / slotSourceCount==1 byte-copy fast paths, called against
        /// a reader opened on the destination writer's just-written bytes.
        /// </summary>
        private static void AddSlotKeysToBloom<TBloomReader, TBloomPin>(
            scoped in TBloomReader reader, Bound slotScope, ulong addrKey, BloomFilter bloom)
            where TBloomPin : struct, IBufferPin, allows ref struct
            where TBloomReader : IHsstByteReader<TBloomPin>, allows ref struct
        {
            Span<byte> slotKey = stackalloc byte[32];
            HsstEnumerator<TBloomReader, TBloomPin> outerEnum = new(in reader, slotScope);
            while (outerEnum.MoveNext(in reader))
            {
                outerEnum.CopyCurrentLogicalKey(in reader, slotKey[..30]);
                Bound innerScope = outerEnum.CurrentValue;
                // The outer entry's value is a keys-first TwoByteSlotValue / -Large sub-slot blob.
                HsstEnumerator<TBloomReader, TBloomPin> innerEnum = HsstEnumerator<TBloomReader, TBloomPin>.CreateTwoByteSlot(in reader, innerScope);
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
        /// Per-call scratch for <see cref="SlotPrefixValueMerger"/>: holds the buffers
        /// reused across outer keys of a single slot-prefix merge driven from
        /// <see cref="MergeSlots"/>. One instance per per-address slot-prefix merge;
        /// held by reference on the value-merger struct so callbacks can reach it
        /// across method boundaries.
        /// </summary>
        private sealed class SlotPrefixValueMergerScratch : IDisposable
        {
            public readonly byte[] SlotKeyBuf;
            public readonly Bound[] InnerBoundsScratch;
            public readonly ArrayPoolList<WholeReadSessionMergeSource> InnerSources;
            public readonly ArrayPoolList<HsstEnumerator> InnerEnumerators;
            public readonly ArrayPoolList<byte> ScratchValues;
            public readonly ArrayPoolList<byte> ScratchKeys;
            public readonly ArrayPoolList<int> ScratchLens;

            public SlotPrefixValueMergerScratch(int n)
            {
                const int InnerKeyLen = 2;
                SlotKeyBuf = new byte[32];
                InnerBoundsScratch = new Bound[n];
                InnerSources = new ArrayPoolList<WholeReadSessionMergeSource>(n, n);
                InnerEnumerators = new ArrayPoolList<HsstEnumerator>(n, n);
                ScratchValues = new ArrayPoolList<byte>(512);
                ScratchKeys = new ArrayPoolList<byte>(Math.Max(1, n) * InnerKeyLen);
                ScratchLens = new ArrayPoolList<int>(Math.Max(1, n));
            }

            public void Dispose()
            {
                InnerSources.Dispose();
                InnerEnumerators.Dispose();
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
            : IHsstBTreeValueMerger<PooledByteBufferWriter.Writer, WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory>
        {
            private const int OuterKeyLen = 30;
            private const int InnerKeyLen = 2;

            public void OnKey(scoped ReadOnlySpan<byte> key) { }

            public void OnFastCopy(scoped ReadOnlySpan<byte> key,
                scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> cursor)
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
                scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> cursor)
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
                Span<HsstEnumerator> innerEnumerators = scratch.InnerEnumerators.AsSpan()[..matchCount];
                NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TwoByteSlotEnumeratorFactory> innerCursor =
                    BuildMergeCursor(cursor.Sources, matchingSources, innerBounds, innerSources, innerEnumerators, innerState, InnerKeyLen,
                        default(TwoByteSlotEnumeratorFactory));
                HsstTwoByteSlotMerger.NWayMerge<
                    PooledByteBufferWriter.Writer, WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TwoByteSlotEnumeratorFactory,
                    SlotSuffixBloomCallback>(
                        ref writer, ref innerCursor,
                        scratch.ScratchKeys, scratch.ScratchValues, scratch.ScratchLens,
                        new SlotSuffixBloomCallback(bloom, addrBloomKey, scratch.SlotKeyBuf));
            }

            /// <summary>Per-key bloom callback for the inner 2-byte slot-suffix merge:
            /// concatenates <c>slotKeyBuf[0..30) | innerKey</c> and adds the slot bloom
            /// hash. <c>slotKeyBuf[0..30)</c> is populated by <see cref="MergeValues"/>
            /// from the outer 30-byte key before invoking
            /// <see cref="HsstTwoByteSlotMerger.NWayMerge"/>.</summary>
            private readonly struct SlotSuffixBloomCallback(
                BloomFilter bloom, ulong addrBloomKey, byte[] slotKeyBuf)
                : IHsstTwoByteSlotMergeCallback
            {
                public void OnKey(scoped ReadOnlySpan<byte> key)
                {
                    key.CopyTo(slotKeyBuf.AsSpan(30, 2));
                    bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, slotKeyBuf));
                }
            }

            /// <summary>Front-byte dispatch for the keys-first two-byte-slot variants, whose
            /// <see cref="IndexType"/> byte sits at byte 0 of the scope rather than the tail.
            /// Forwards to <see cref="HsstEnumerator.CreateTwoByteSlot"/>.</summary>
            private readonly struct TwoByteSlotEnumeratorFactory : IHsstEnumeratorFactory<WholeReadSessionReader, NoOpPin>
            {
                public HsstEnumerator Create(scoped in WholeReadSessionReader reader, Bound bound)
                    => HsstEnumerator.CreateTwoByteSlot(in reader, bound);
            }
        }
    }

    /// <summary>BTree value merger for the storage-trie column (tag 0x05). No per-outer-key
    /// bloom add (only slot keys are bloomed). On a fast-copied source value walks the
    /// three storage-trie sub-tags (top / compact / fallback) for per-node bloom adds. On a
    /// multi-source (or oversized single-source) rebuild assembles a fresh per-addressHash
    /// DenseByteIndex with the three sub-tag merges emitted in descending tag order via
    /// <see cref="MergeStorageSubTag"/> (one call per sub-tag with the matching
    /// <c>subTag</c> + <c>innerKeySize</c> pair).</summary>
    /// <remarks>Cursor-side reader/pin are pinned to (<see cref="WholeReadSessionReader"/>,
    /// <see cref="NoOpPin"/>); the three generic parameters are the WRITER-side trio
    /// threaded through to the inner PackedArray builder per sub-tag. Per-source reader
    /// factories come via the cursor (<c>cursor.CreateMinReader</c>,
    /// <c>cursor.Sources</c>); no <c>_views</c> field is needed.</remarks>
    private readonly struct StorageTrieColumnValueMerger<TWriter, TReader, TPin>(BloomFilter bloom)
        : IHsstBTreeValueMerger<TWriter, WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory>
        where TWriter : IByteBufferWriterWithReader<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        public void OnKey(scoped ReadOnlySpan<byte> key) { }

        public void OnFastCopy(scoped ReadOnlySpan<byte> key,
            scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> cursor)
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
            scoped ref NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> cursor)
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

            HsstDenseByteIndexBuilder<TWriter> perAddrBuilder = new(ref writer);
            try
            {
                // Emit descending 0x02 (Fallback) → 0x01 (Compact) → 0x00 (Top).
                MergeStorageSubTag(cursor.Sources, matchingSources, matchCount, subTagBounds,
                    ref perAddrBuilder, PersistedSnapshotTags.StorageFallbackSubTag, innerKeySize: 33, addrKey);
                MergeStorageSubTag(cursor.Sources, matchingSources, matchCount, subTagBounds,
                    ref perAddrBuilder, PersistedSnapshotTags.StorageCompactSubTag, innerKeySize: 8, addrKey);
                MergeStorageSubTag(cursor.Sources, matchingSources, matchCount, subTagBounds,
                    ref perAddrBuilder, PersistedSnapshotTags.StorageTopSubTag, innerKeySize: 4, addrKey);
                perAddrBuilder.Build();
            }
            finally
            {
                perAddrBuilder.Dispose();
            }
        }

        /// <summary>Merges one storage-trie sub-tag (top / compact / fallback) into
        /// <paramref name="perAddrBuilder"/>. Single-source: byte-copy the source's sub-tag
        /// blob verbatim and walk it for bloom adds. Multi-source: streaming N-way merge
        /// into a fixed-size PackedArray (NodeRef.Size value, <paramref name="innerKeySize"/>
        /// key); newest wins on key collision (storage trie nodes are content-addressable
        /// so duplicate keys carry identical NodeRefs in practice).
        /// <paramref name="subTag"/> selects the column (and its index byte) and
        /// <paramref name="innerKeySize"/> selects the inner key width (33 / 8 / 4 for
        /// Fallback / Compact / Top).</summary>
        private void MergeStorageSubTag(
            ReadOnlySpan<WholeReadSessionMergeSource> sources,
            ReadOnlySpan<int> matchingSources, int matchCount,
            ReadOnlySpan<Bound> subTagBounds,
            scoped ref HsstDenseByteIndexBuilder<TWriter> perAddrBuilder,
            byte[] subTag, int innerKeySize,
            ulong addrKey)
        {
            int subTagIdx = subTag[0];
            const int PerSourceStride = PersistedSnapshotTags.StorageTrieSubTagCount;

            using NativeMemoryListRef<int> srcsList = new(matchCount, matchCount);
            using NativeMemoryListRef<Bound> boundsList = new(matchCount, matchCount);
            Span<int> srcs = srcsList.AsSpan();
            Span<Bound> subBounds = boundsList.AsSpan();

            int active = 0;
            for (int j = 0; j < matchCount; j++)
            {
                Bound sb = subTagBounds[j * PerSourceStride + subTagIdx];
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
                WholeReadSessionReader r = sources[matchingSources[j]].CreateReader();
                using NoOpPin pin = r.PinBuffer(subBounds[0].Offset, subBounds[0].Length);
                perAddrBuilder.Add(subTag, pin.Buffer);
                AddStorageTrieKeysToBloom<WholeReadSessionReader, NoOpPin>(in r, subBounds[0], addrKey, bloom);
                return;
            }

            using LoserTreeState state = new(active, innerKeySize);
            using ArrayPoolList<WholeReadSessionMergeSource> innerSourcesList = new(active, active);
            using ArrayPoolList<HsstEnumerator> innerEnumeratorsList = new(active, active);
            Span<WholeReadSessionMergeSource> innerSources = innerSourcesList.AsSpan();
            Span<HsstEnumerator> innerEnumerators = innerEnumeratorsList.AsSpan();

            Span<int> outerIndices = stackalloc int[active];
            for (int j = 0; j < active; j++) outerIndices[j] = matchingSources[srcs[j]];
            NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> innerCursor =
                BuildMergeCursor(sources, outerIndices, subBounds[..active], innerSources, innerEnumerators, state, innerKeySize,
                    default(TailDispatchEnumeratorFactory));

            ref TWriter subWriter = ref perAddrBuilder.BeginValueWrite();
            HsstPackedArrayMerger.NWayMerge<TWriter, WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory, AddrXorStatePathBloomCallback>(
                ref subWriter, NodeRef.Size, ref innerCursor, new AddrXorStatePathBloomCallback(bloom, addrKey));
            perAddrBuilder.FinishValueWrite(subTag);
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

        /// <summary>
        /// Walk a storage-trie sub-tag HSST (top / compact / fallback — keys are 4 / 8 /
        /// 33 bytes respectively) and add <c>StorageNodeKey(addressHash, path)</c> to
        /// <paramref name="bloom"/> for each entry. Mirrors
        /// <see cref="PerAddressColumnValueMerger{TWriter,TReader,TPin}.AddSlotKeysToBloom"/>
        /// for the byte-copy fast paths in this merger's per-sub-tag methods and
        /// <see cref="NWayMergeStorageTrieColumn"/> where the sub-tag bytes are copied
        /// verbatim and the cursor loop does not run.
        /// </summary>
        private static void AddStorageTrieKeysToBloom<TBloomReader, TBloomPin>(
            scoped in TBloomReader reader, Bound subTagScope, ulong addrKey, BloomFilter bloom)
            where TBloomPin : struct, IBufferPin, allows ref struct
            where TBloomReader : IHsstByteReader<TBloomPin>, allows ref struct
        {
            Span<byte> keyBuf = stackalloc byte[33];
            HsstEnumerator<TBloomReader, TBloomPin> e = new(in reader, subTagScope);
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

        // Shared sources buffer for every cursor-using column. Rented once and reused
        // across all five columns — each column re-seeds the buffer at its own column
        // tag (bound resolved by ResolveColumnBound). NWayMetadataMerge below stays on
        // raw views: it reads metadata fields directly through readers, no cursor needed.
        int n = views.Length;
        using ArrayPoolList<WholeReadSessionMergeSource> columnSourcesList = new(n, n);
        Span<WholeReadSessionMergeSource> columnSources = columnSourcesList.AsSpan();

        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            for (int i = 0; i < n; i++)
                columnSources[i] = new(views[i], ResolveColumnBound(views[i], PersistedSnapshotTags.StorageTrieColumnTag));
            NWayMergeStorageTrieColumn<TWriter, TReader, TPin>(columnSources, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StorageTrieColumnTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            for (int i = 0; i < n; i++)
                columnSources[i] = new(views[i], ResolveColumnBound(views[i], PersistedSnapshotTags.StateNodeFallbackTag));
            NWayPackedArrayMerge<TWriter, TReader, TPin>(columnSources, keySize: 33, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StateNodeFallbackTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            for (int i = 0; i < n; i++)
                columnSources[i] = new(views[i], ResolveColumnBound(views[i], PersistedSnapshotTags.StateNodeTag));
            NWayPackedArrayMerge<TWriter, TReader, TPin>(columnSources, keySize: 8, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StateNodeTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            for (int i = 0; i < n; i++)
                columnSources[i] = new(views[i], ResolveColumnBound(views[i], PersistedSnapshotTags.StateTopNodesTag));
            NWayPackedArrayMerge<TWriter, TReader, TPin>(columnSources, keySize: 4, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StateTopNodesTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            for (int i = 0; i < n; i++)
                columnSources[i] = new(views[i], ResolveColumnBound(views[i], PersistedSnapshotTags.AccountColumnTag));
            NWayMergePerAddressColumn<TWriter, TReader, TPin>(columnSources, ref valueWriter, bloom);
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
    /// N-way streaming merge of a column across N pre-seeded sources into a fixed-key-size
    /// PackedArray HSST. On key collision, newest (highest index) wins. The caller owns
    /// view-seeding and source disposal — pass a <see cref="Span{T}"/> of
    /// <see cref="WholeReadSessionMergeSource"/> whose bound is the column tag's scope
    /// (resolved e.g. via <see cref="ResolveColumnBound"/>).
    /// </summary>
    private static void NWayPackedArrayMerge<TWriter, TReader, TPin>(
        Span<WholeReadSessionMergeSource> sources, int keySize,
        ref TWriter writer, BloomFilter bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(bloom);
        int n = sources.Length;
        // Cache each source's current logical key once per MoveNext so the O(log N) cursor
        // and O(N) match-detection scans don't redo CopyCurrentLogicalKey per output key.
        int keyStride = Math.Max(1, keySize);
        using LoserTreeState state = new(n, keyStride);
        using ArrayPoolList<HsstEnumerator> enumeratorsList = new(n, n);
        Span<HsstEnumerator> enumerators = enumeratorsList.AsSpan();
        NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> cursor =
            new(sources, enumerators, state, keySize);

        HsstPackedArrayMerger.NWayMerge<TWriter, WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory, StatePathBloomCallback>(
            ref writer, NodeRef.Size, ref cursor, new StatePathBloomCallback(bloom));
    }
    /// <summary>
    /// N-way merge of the per-address column (tag 0x01) across N snapshots.
    /// Outer: raw 20-byte Address keys (minSep=4). A single matching source
    /// whose per-address HSST entry (key + value) fits one page and can be page-
    /// aligned at the current writer position byte-copies through
    /// <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}.TryAddAligned"/>
    /// (HSST internal pointers are HSST-relative, so a relocation stays readable);
    /// larger entries, unalignable positions, and any multi-source collision fall
    /// through to <see cref="PerAddressColumnValueMerger{TWriter,TReader,TPin}.MergeValues"/>,
    /// which re-emits per sub-tag.
    /// Per-address inner sub-tags are 0x00 (account RLP), 0x01 (self-destruct),
    /// 0x02 (slots). Storage-trie nodes live in column 0x05 keyed by addressHash
    /// and are merged separately by <see cref="NWayMergeStorageTrieColumn"/>.
    /// </summary>
    private static void NWayMergePerAddressColumn<TWriter, TReader, TPin>(
        Span<WholeReadSessionMergeSource> sources, ref TWriter writer, BloomFilter bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = sources.Length;
        // Cache each source's current 20-byte Address key (stride 32 with room).
        const int KeyStride = 32;
        const int AddrKeyLen = PersistedSnapshotTags.AddressKeyLength;
        using LoserTreeState state = new(n, KeyStride);

        // Reusable work buffers for the per-address slot prefix/suffix HSST builders.
        // The container is a class so the value-merger can hold it as a regular field; the
        // contained buffers live across every merged address — the prefix builder is created
        // once per address and the suffix builder once per prefix group per address, so
        // amortising the rentals matters.
        using HsstBTreeBuilderBuffersContainer slotPrefixBuffers = new();
        using ArrayPoolList<HsstEnumerator> enumeratorsList = new(n, n);
        Span<HsstEnumerator> enumerators = enumeratorsList.AsSpan();

        NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> cursor =
            new(sources, enumerators, state, AddrKeyLen);

        PerAddressColumnValueMerger<TWriter, TReader, TPin> valueMerger =
            new(bloom, slotPrefixBuffers);
        HsstBTreeMerger.NWayMerge<TWriter, TReader, TPin,
            WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory,
            PerAddressColumnValueMerger<TWriter, TReader, TPin>>(
            ref writer, AddrKeyLen, ref cursor, valueMerger);
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
    /// 0x02 → 0x01 → 0x00) via dedicated per-sub-tag methods on
    /// <see cref="StorageTrieColumnValueMerger{TWriter,TReader,TPin}"/>, each streaming
    /// the inner-PackedArray merge for its sub-tag.
    /// </summary>
    private static void NWayMergeStorageTrieColumn<TWriter, TReader, TPin>(
        Span<WholeReadSessionMergeSource> sources, ref TWriter writer, BloomFilter bloom) where TWriter : IByteBufferWriterWithReader<TReader, TPin> where TReader : IHsstByteReader<TPin>, allows ref struct where TPin : struct, IBufferPin, allows ref struct
    {
        int n = sources.Length;
        const int KeyStride = 32;
        const int AddrKeyLen = PersistedSnapshotTags.AddressHashPrefixLength;
        using LoserTreeState state = new(n, KeyStride);
        using ArrayPoolList<HsstEnumerator> enumeratorsList = new(n, n);
        Span<HsstEnumerator> enumerators = enumeratorsList.AsSpan();
        NWayMergeCursor<WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory> cursor =
            new(sources, enumerators, state, AddrKeyLen);

        StorageTrieColumnValueMerger<TWriter, TReader, TPin> valueMerger = new(bloom);
        HsstBTreeMerger.NWayMerge<TWriter, TReader, TPin,
            WholeReadSessionReader, NoOpPin, WholeReadSessionMergeSource, TailDispatchEnumeratorFactory,
            StorageTrieColumnValueMerger<TWriter, TReader, TPin>>(
            ref writer, AddrKeyLen, ref cursor, valueMerger);
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

}
