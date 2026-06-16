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
/// <remarks>
/// The merge is generic over the byte-reader source so it isn't bound to a specific reader:
/// each input is an <see cref="IHsstReaderSource{TReader,TPin}"/> (<typeparamref name="TView"/>)
/// that mints a fresh reader on demand. Production drives it with
/// <see cref="WholeReadSession"/> / <see cref="WholeReadSessionReader"/>.
/// </remarks>
public static class PersistedSnapshotMerger
{
    /// <summary>
    /// One source for <see cref="NWayMergeCursor{TReader,TPin,TSource,TFactory}"/>: a reader
    /// source (<typeparamref name="TView"/>) that recreates a fresh reader each time the cursor
    /// advances, plus the <see cref="Bound"/> scope this slot is positioned over. Built once per
    /// cursor slot at merge setup; the cursor copies it by value into its sources span.
    /// </summary>
    private readonly struct ViewMergeSource<TView, TReader, TPin>(TView view, Bound bound)
        : IHsstMergeSource<TReader, TPin>
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        public TReader CreateReader() => view.CreateReader();
        public Bound Bound => bound;

        /// <summary>Re-seed at a different bound (same view). Used by
        /// <see cref="BuildMergeCursor{TView,TReader,TPin,TFactory}"/> in nested-merge re-seeds.</summary>
        public ViewMergeSource<TView, TReader, TPin> WithBound(Bound newBound) => new(view, newBound);
    }

    /// <summary>Open a fresh reader on <paramref name="view"/>, seek the root HSST for
    /// <paramref name="columnTag"/>, and return its bound (or an empty bound if the tag
    /// is absent — sources at the empty bound are treated as exhausted on first
    /// MoveNext).</summary>
    private static Bound ResolveColumnBound<TView, TReader, TPin>(TView view, byte[] columnTag)
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        TReader r = view.CreateReader();
        HsstReader<TReader, TPin> hsst = new(in r, new Bound(0, r.Length));
        return hsst.TrySeek(columnTag, out Bound b) ? b : default;
    }

    /// <summary>Tail-byte dispatch: <c>new HsstEnumerator(in reader, bound)</c> reads the
    /// trailing <see cref="IndexType"/> byte to pick PackedArray / BTree / BTreeKeyFirst.</summary>
    private readonly struct TailDispatchEnumeratorFactory<TReader, TPin> : IHsstEnumeratorFactory<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        public HsstEnumerator<TReader, TPin> Create(scoped in TReader reader, Bound bound)
            => new(in reader, bound);
    }

    /// <summary>
    /// Re-seeds <paramref name="indices"/>.Length sources by cloning entries of
    /// <paramref name="outerSources"/> at the matching <paramref name="innerBounds"/>,
    /// writing them into <paramref name="sourcesBuf"/>, and returning a cursor over the
    /// result. Each clone shares the original source's view with a rewritten
    /// <see cref="Bound"/>; the cursor constructs the per-slot
    /// <see cref="HsstEnumerator{TReader,TPin}"/> via <typeparamref name="TFactory"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="indices"/>, <paramref name="innerBounds"/>,
    /// <paramref name="sourcesBuf"/>, and <paramref name="enumeratorsBuf"/> must each have
    /// at least <paramref name="indices"/>.Length elements.
    /// </remarks>
    private static NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TFactory>
        BuildMergeCursor<TView, TReader, TPin, TFactory>(
            ReadOnlySpan<ViewMergeSource<TView, TReader, TPin>> outerSources,
            ReadOnlySpan<int> indices,
            ReadOnlySpan<Bound> innerBounds,
            Span<ViewMergeSource<TView, TReader, TPin>> sourcesBuf,
            Span<HsstEnumerator<TReader, TPin>> enumeratorsBuf,
            LoserTreeState state,
            int keyLen,
            TFactory factory = default)
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
        where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
    {
        for (int j = 0; j < indices.Length; j++)
            sourcesBuf[j] = outerSources[indices[j]].WithBound(innerBounds[j]);
        return new NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TFactory>(
            sourcesBuf[..indices.Length], enumeratorsBuf[..indices.Length], state, keyLen, factory);
    }

    /// <summary>For each matching source in <paramref name="cursor"/>'s <c>MatchingSources</c>,
    /// captures the per-source per-address bound from the cursor's current value AND resolves
    /// the per-source sub-tag bounds via <see cref="HsstDenseByteIndexReader.TryResolveAll"/>.
    /// Shared by both BTree value-mergers (per-address column 0x01 with
    /// <c>PerAddrSubTagCount</c> sub-tags, storage-trie column 0x05 with
    /// <c>StorageTrieSubTagCount</c> sub-tags). Caller allocates the output spans sized
    /// <c>matchCount</c> and <c>matchCount * subTagCount</c> respectively.</summary>
    private static void ResolvePerAddrAndSubTagBounds<TView, TReader, TPin>(
        scoped in NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>> cursor,
        scoped Span<Bound> perAddrBounds, scoped Span<Bound> subTagBounds, int subTagCount)
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        ReadOnlySpan<int> matchingSources = cursor.MatchingSources;
        Span<ViewMergeSource<TView, TReader, TPin>> sources = cursor.Sources;
        for (int j = 0; j < matchingSources.Length; j++)
        {
            perAddrBounds[j] = cursor.ValueAt(matchingSources[j]);
            TReader r = sources[matchingSources[j]].CreateReader();
            HsstDenseByteIndexReader.TryResolveAll<TReader, TPin>(
                in r, perAddrBounds[j],
                subTagBounds.Slice(j * subTagCount, subTagCount));
        }
    }

    private readonly struct StatePathBloomCallback(BloomFilter bloom)
        : IHsstMergeKeyCallback
    {
        public void OnKey(scoped ReadOnlySpan<byte> key)
            => bloom.Add(PersistedSnapshotBloomBuilder.StatePathKey(key));
    }

    /// <summary>BTree value merger for the per-address column (tag 0x01). On every emitted
    /// outer key adds <c>addrKey</c> to the bloom, resolves each contributing source's
    /// per-address bounds and per-source sub-tag bounds, then streams the merged per-address
    /// DenseByteIndex (sub-tags 0x02 Slots, 0x01 SelfDestruct, 0x00 Account) through the outer
    /// builder's value writer.</summary>
    /// <remarks>The shared <see cref="HsstBTreeBuilderBuffers"/> arena (re-used across every
    /// emitted address) is held via <see cref="HsstBTreeBuilderBuffers.Container"/> — a class
    /// handle that hides the ref-to-ref-struct workaround.</remarks>
    private readonly struct PerAddressColumnValueMerger<TWriter, TView, TReader, TPin>(
        BloomFilter bloom, HsstBTreeBuilderBuffers.Container slotPrefixBuffers)
        : IHsstBTreeValueMerger<TWriter, TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>>
        where TWriter : IByteBufferWriter
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        public void MergeValues(scoped ref HsstBTreeBuilder<TWriter> builder, scoped ReadOnlySpan<byte> key,
            scoped in NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>> cursor)
        {
            ulong addrKey = MemoryMarshal.Read<ulong>(key);
            bloom.Add(addrKey);
            ReadOnlySpan<int> matchingSources = cursor.MatchingSources;
            int matchCount = matchingSources.Length;
            const int SubTagCount = PersistedSnapshotTags.PerAddrSubTagCount;

            Span<Bound> perAddrBounds = stackalloc Bound[matchCount];
            Span<Bound> subTagBounds = stackalloc Bound[matchCount * SubTagCount];
            ResolvePerAddrAndSubTagBounds(in cursor, perAddrBounds, subTagBounds, SubTagCount);

            // Single-source, no-slot fast path: slots are the only per-address sub-tag re-emitted
            // (through a page-aligning inner BTree) on rebuild; with none present a lone source's
            // DenseByteIndex blob is byte-identical to a rebuild, so copy it verbatim through the
            // outer builder's Add — which page-aligns and leaf-wraps the entry — instead of
            // rebuilding via the streaming BeginValueWrite path.
            int slotTag = PersistedSnapshotTags.SlotSubTag[0];
            if (matchCount == 1 && subTagBounds[slotTag].Length == 0) // matchCount==1 => source 0 at index slotTag
            {
                TReader reader = cursor.Sources[matchingSources[0]].CreateReader();
                using TPin pin = reader.PinBuffer(perAddrBounds[0]);
                builder.Add(key, pin.Buffer);
                return;
            }

            ref TWriter writer = ref builder.BeginValueWrite();
            long valueStart = writer.Written;
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
            builder.FinishValueWrite(key, writer.Written - valueStart);
        }

        /// <summary>Sub-tag 0x02: emit the merged slot HSST. Finds the newest destruct
        /// barrier (newest source where SelfDestructSubTag is destructed-marked), then
        /// drives an outer 30-byte slot-prefix keyFirst BTree merge over slot-bearing
        /// sources from <c>max(0, destructBarrier)..matchCount-1</c> via
        /// <see cref="HsstBTreeMerger.NWayMerge"/> (<c>keyFirst: true</c>) with
        /// <see cref="SlotPrefixValueMerger"/> handling the inner 2-byte suffix merge.
        /// We do not byte-copy a single-source slot blob through perAddrBuilder here:
        /// the dense byte index does not page-align its values, so re-emitting through
        /// the inner BTree builder (which does align) keeps the slot HSST on its own
        /// page.</summary>
        private void MergeSlots(
            ReadOnlySpan<ViewMergeSource<TView, TReader, TPin>> sources,
            ReadOnlySpan<int> matchingSources, int matchCount,
            scoped ReadOnlySpan<Bound> subTagBounds,
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
                TReader r = sources[matchingSources[j]].CreateReader();
                using TPin sdPin = r.PinBuffer(new Bound(sdb.Offset, 1));
                if (sdPin.Buffer[0] == PersistedSnapshotTags.SelfDestructDestructedMarkerByte)
                    destructBarrier = j;
            }

            int slotStart = Math.Max(0, destructBarrier);
            int slotTag = PersistedSnapshotTags.SlotSubTag[0];
            int slotSourceCount = 0;
            int slotCapacity = matchCount - slotStart;
            Span<int> slotSources = stackalloc int[slotCapacity];
            Span<Bound> slotBounds = stackalloc Bound[slotCapacity];
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
                using ArrayPoolList<ViewMergeSource<TView, TReader, TPin>> slotPrefixSourcesList = new(slotSourceCount, slotSourceCount);
                using ArrayPoolList<HsstEnumerator<TReader, TPin>> slotPrefixEnumeratorsList = new(slotSourceCount, slotSourceCount);
                Span<ViewMergeSource<TView, TReader, TPin>> slotPrefixSources = slotPrefixSourcesList.AsSpan();
                Span<HsstEnumerator<TReader, TPin>> slotPrefixEnumerators = slotPrefixEnumeratorsList.AsSpan();

                NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>> outerCursor =
                    BuildMergeCursor(sources, slotSources[..slotSourceCount], slotBounds[..slotSourceCount],
                        slotPrefixSources, slotPrefixEnumerators, outerState, OuterKeyLen,
                        default(TailDispatchEnumeratorFactory<TReader, TPin>));

                ref TWriter slotWriter = ref perAddrBuilder.BeginValueWrite();
                HsstBTreeMerger.NWayMerge<
                    TWriter,
                    TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>,
                    SlotPrefixValueMerger>(
                        ref slotWriter, OuterKeyLen, ref outerCursor,
                        new SlotPrefixValueMerger(bloom, addrKey, scratch),
                        ref slotPrefixBuffers.Buffers, keyFirst: true);
                perAddrBuilder.FinishValueWrite(PersistedSnapshotTags.SlotSubTag);
            }
        }

        /// <summary>Sub-tag 0x01: iterate sources 0..M-1, apply TryAdd semantics
        /// (newer=destructed [0x00] wins; newer=new [0x01] keeps the older). Presence is
        /// signalled by length>0; absent entries (gap-filled length 0 under DenseByteIndex)
        /// are ignored. Track the winning bound snapshot-absolute so we can re-pin at the
        /// end without holding a span across iterations.</summary>
        private void MergeSelfDestruct(
            ReadOnlySpan<ViewMergeSource<TView, TReader, TPin>> sources,
            ReadOnlySpan<int> matchingSources, int matchCount,
            scoped ReadOnlySpan<Bound> subTagBounds,
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
                    TReader r = sources[matchingSources[j]].CreateReader();
                    using TPin firstBytePin = r.PinBuffer(new Bound(sdb.Offset, 1));
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
                TReader r = sources[matchingSources[sdSrcJ]].CreateReader();
                using TPin sdPin = r.PinBuffer(new Bound(sdValOff, sdValLen));
                perAddrBuilder.Add(PersistedSnapshotTags.SelfDestructSubTag, sdPin.Buffer);
            }
        }

        /// <summary>Sub-tag 0x00: newest wins. Walk M-1..0, first present (length>0).
        /// Emitted last so the hot Account blob lands adjacent to the DenseByteIndex
        /// Ends[] trailer.</summary>
        private void MergeAccount(
            ReadOnlySpan<ViewMergeSource<TView, TReader, TPin>> sources,
            ReadOnlySpan<int> matchingSources, int matchCount,
            scoped ReadOnlySpan<Bound> subTagBounds,
            scoped ref HsstDenseByteIndexBuilder<TWriter> perAddrBuilder)
        {
            int acctTag = PersistedSnapshotTags.AccountSubTag[0];
            for (int j = matchCount - 1; j >= 0; j--)
            {
                Bound ab = subTagBounds[j * PersistedSnapshotTags.PerAddrSubTagCount + acctTag];
                if (ab.Length == 0) continue;
                TReader r = sources[matchingSources[j]].CreateReader();
                using TPin acctPin = r.PinBuffer(ab);
                perAddrBuilder.Add(PersistedSnapshotTags.AccountSubTag, acctPin.Buffer);
                break;
            }
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
            public readonly ArrayPoolList<ViewMergeSource<TView, TReader, TPin>> InnerSources;
            public readonly ArrayPoolList<HsstEnumerator<TReader, TPin>> InnerEnumerators;
            public readonly NativeMemoryList<byte> ScratchValues;
            public readonly NativeMemoryList<byte> ScratchKeys;
            public readonly NativeMemoryList<int> ScratchLens;
            /// <summary>Staging buffer for the inner slot HSST, reused across outer keys; the
            /// keyFirst outer builder needs the full value before <c>Add</c>.</summary>
            public readonly PooledByteBufferWriter Staging;

            public SlotPrefixValueMergerScratch(int n)
            {
                const int InnerKeyLen = 2;
                SlotKeyBuf = new byte[32];
                InnerBoundsScratch = new Bound[n];
                InnerSources = new ArrayPoolList<ViewMergeSource<TView, TReader, TPin>>(n, n);
                InnerEnumerators = new ArrayPoolList<HsstEnumerator<TReader, TPin>>(n, n);
                ScratchValues = new NativeMemoryList<byte>(512);
                ScratchKeys = new NativeMemoryList<byte>(Math.Max(1, n) * InnerKeyLen);
                ScratchLens = new NativeMemoryList<int>(Math.Max(1, n));
                Staging = new PooledByteBufferWriter(4096);
            }

            public void Dispose()
            {
                InnerSources.Dispose();
                InnerEnumerators.Dispose();
                ScratchValues.Dispose();
                ScratchKeys.Dispose();
                ScratchLens.Dispose();
                Staging.Dispose();
            }
        }

        /// <summary>
        /// BTree value merger for the per-address slot-prefix column. Outer is a keyFirst
        /// 30-byte BTree of slot prefixes; each outer entry's value is a keys-first
        /// TwoByteSlotValue / TwoByteSlotValueLarge HSST of the remaining 2-byte slot
        /// suffixes. Drives the inner 2-byte merge from the matched outer sources,
        /// buffers merged keys/values into the scratch, picks the inner format by total
        /// payload size, stages the chosen blob, and adds it to the keyFirst outer builder.
        /// </summary>
        /// <remarks>
        /// The keyFirst BTree builder needs the value length up front, so this merger stages the
        /// inner blob through the scratch's <see cref="PooledByteBufferWriter"/> and then calls
        /// <c>builder.Add(key, stagedSpan)</c> rather than streaming via
        /// <see cref="HsstBTreeBuilder{TWriter}.BeginValueWrite"/>. The scratch lives on a class so
        /// this struct can hold it by reference across the
        /// <see cref="IHsstBTreeValueMerger{TWriter,TReader,TPin,TSource,TFactory}"/> callbacks.
        /// </remarks>
        private readonly struct SlotPrefixValueMerger(
            BloomFilter bloom, ulong addrBloomKey, SlotPrefixValueMergerScratch scratch)
            : IHsstBTreeValueMerger<TWriter, TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>>
        {
            private const int OuterKeyLen = 30;
            private const int InnerKeyLen = 2;

            public void MergeValues(scoped ref HsstBTreeBuilder<TWriter> builder, scoped ReadOnlySpan<byte> key,
                scoped in NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>> cursor)
            {
                int matchCount = cursor.MatchCount;
                ReadOnlySpan<int> matchingSources = cursor.MatchingSources;
                Span<byte> slotKeyBuf = scratch.SlotKeyBuf;
                key.CopyTo(slotKeyBuf[..OuterKeyLen]);

                using LoserTreeState innerState = new(matchCount, InnerKeyLen);
                Span<Bound> innerBounds = scratch.InnerBoundsScratch.AsSpan(0, matchCount);
                for (int k = 0; k < matchCount; k++)
                    innerBounds[k] = cursor.ValueAt(matchingSources[k]);
                Span<ViewMergeSource<TView, TReader, TPin>> innerSources = scratch.InnerSources.AsSpan()[..matchCount];
                Span<HsstEnumerator<TReader, TPin>> innerEnumerators = scratch.InnerEnumerators.AsSpan()[..matchCount];
                NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TwoByteSlotEnumeratorFactory> innerCursor =
                    BuildMergeCursor(cursor.Sources, matchingSources, innerBounds, innerSources, innerEnumerators, innerState, InnerKeyLen,
                        default(TwoByteSlotEnumeratorFactory));

                // keyFirst outer needs the value length up front: stage the inner blob, then add it whole.
                PooledByteBufferWriter staging = scratch.Staging;
                staging.Reset();
                ref PooledByteBufferWriter.Writer stagingWriter = ref staging.GetWriter();
                HsstTwoByteSlotMerger.NWayMerge<
                    PooledByteBufferWriter.Writer, TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TwoByteSlotEnumeratorFactory,
                    SlotSuffixBloomCallback>(
                        ref stagingWriter, ref innerCursor,
                        scratch.ScratchKeys, scratch.ScratchValues, scratch.ScratchLens,
                        new SlotSuffixBloomCallback(bloom, addrBloomKey, scratch.SlotKeyBuf));
                builder.Add(key, staging.WrittenSpan);
            }

            /// <summary>Per-key bloom callback for the inner 2-byte slot-suffix merge:
            /// concatenates <c>slotKeyBuf[0..30) | innerKey</c> and adds the slot bloom
            /// hash. <c>slotKeyBuf[0..30)</c> is populated by <see cref="MergeValues"/>
            /// from the outer 30-byte key before invoking
            /// <see cref="HsstTwoByteSlotMerger.NWayMerge"/>.</summary>
            private readonly struct SlotSuffixBloomCallback(
                BloomFilter bloom, ulong addrBloomKey, byte[] slotKeyBuf)
                : IHsstMergeKeyCallback
            {
                public void OnKey(scoped ReadOnlySpan<byte> key)
                {
                    key.CopyTo(slotKeyBuf.AsSpan(30, 2));
                    bloom.Add(PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, slotKeyBuf));
                }
            }

            /// <summary>Front-byte dispatch for the keys-first two-byte-slot variants, whose
            /// <see cref="IndexType"/> byte sits at byte 0 of the scope rather than the tail.
            /// Forwards to <see cref="HsstEnumerator{TReader,TPin}.CreateTwoByteSlot"/>.</summary>
            private readonly struct TwoByteSlotEnumeratorFactory : IHsstEnumeratorFactory<TReader, TPin>
            {
                public HsstEnumerator<TReader, TPin> Create(scoped in TReader reader, Bound bound)
                    => HsstEnumerator<TReader, TPin>.CreateTwoByteSlot(in reader, bound);
            }
        }
    }

    /// <summary>BTree value merger for the storage-trie column (tag 0x05). No per-outer-key
    /// bloom add; per-node bloom adds happen inside each sub-tag merge. Assembles a fresh
    /// per-addressHash DenseByteIndex with the three storage-trie sub-tag merges (top /
    /// compact / fallback) emitted in descending tag order via
    /// <see cref="MergeStorageSubTag"/> (one call per sub-tag with the matching
    /// <c>subTag</c> + <c>innerKeySize</c> pair).</summary>
    private readonly struct StorageTrieColumnValueMerger<TWriter, TView, TReader, TPin>(BloomFilter bloom)
        : IHsstBTreeValueMerger<TWriter, TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>>
        where TWriter : IByteBufferWriter
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        public void MergeValues(scoped ref HsstBTreeBuilder<TWriter> builder, scoped ReadOnlySpan<byte> key,
            scoped in NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>> cursor)
        {
            ulong addrKey = MemoryMarshal.Read<ulong>(key);
            ReadOnlySpan<int> matchingSources = cursor.MatchingSources;
            int matchCount = matchingSources.Length;
            const int SubTagCount = PersistedSnapshotTags.StorageTrieSubTagCount;

            Span<Bound> perAddrBounds = stackalloc Bound[matchCount];
            Span<Bound> subTagBounds = stackalloc Bound[matchCount * SubTagCount];
            ResolvePerAddrAndSubTagBounds(in cursor, perAddrBounds, subTagBounds, SubTagCount);

            ref TWriter writer = ref builder.BeginValueWrite();
            long valueStart = writer.Written;
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
            builder.FinishValueWrite(key, writer.Written - valueStart);
        }

        /// <summary>Merges one storage-trie sub-tag (top / compact / fallback) into
        /// <paramref name="perAddrBuilder"/> via a streaming N-way merge into a fixed-size
        /// PackedArray (NodeRef.Size value, <paramref name="innerKeySize"/> key); newest wins
        /// on key collision (storage trie nodes are content-addressable so duplicate keys
        /// carry identical NodeRefs in practice).
        /// <paramref name="subTag"/> selects the column (and its index byte) and
        /// <paramref name="innerKeySize"/> selects the inner key width (33 / 8 / 4 for
        /// Fallback / Compact / Top).</summary>
        private void MergeStorageSubTag(
            ReadOnlySpan<ViewMergeSource<TView, TReader, TPin>> sources,
            ReadOnlySpan<int> matchingSources, int matchCount,
            scoped ReadOnlySpan<Bound> subTagBounds,
            scoped ref HsstDenseByteIndexBuilder<TWriter> perAddrBuilder,
            byte[] subTag, int innerKeySize,
            ulong addrKey)
        {
            int subTagIdx = subTag[0];
            const int PerSourceStride = PersistedSnapshotTags.StorageTrieSubTagCount;

            Span<int> srcs = stackalloc int[matchCount];
            Span<Bound> subBounds = stackalloc Bound[matchCount];

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

            using LoserTreeState state = new(active, innerKeySize);
            using ArrayPoolList<ViewMergeSource<TView, TReader, TPin>> innerSourcesList = new(active, active);
            using ArrayPoolList<HsstEnumerator<TReader, TPin>> innerEnumeratorsList = new(active, active);
            Span<ViewMergeSource<TView, TReader, TPin>> innerSources = innerSourcesList.AsSpan();
            Span<HsstEnumerator<TReader, TPin>> innerEnumerators = innerEnumeratorsList.AsSpan();

            Span<int> outerIndices = stackalloc int[active];
            for (int j = 0; j < active; j++) outerIndices[j] = matchingSources[srcs[j]];
            NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>> innerCursor =
                BuildMergeCursor(sources, outerIndices, subBounds[..active], innerSources, innerEnumerators, state, innerKeySize,
                    default(TailDispatchEnumeratorFactory<TReader, TPin>));

            ref TWriter subWriter = ref perAddrBuilder.BeginValueWrite();
            HsstPackedArrayMerger.NWayMerge<TWriter, TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>, AddrXorStatePathBloomCallback>(
                ref subWriter, NodeRef.Size, ref innerCursor, new AddrXorStatePathBloomCallback(bloom, addrKey));
            perAddrBuilder.FinishValueWrite(subTag);
        }

        /// <summary>Per-key bloom callback for storage-trie sub-tag merges: adds
        /// <c>addrKey ^ StatePathKey(minKey)</c> to <paramref name="bloom"/>, mixing the
        /// per-addressHash key prefix so colliding TreePath keys in different addresses don't
        /// alias in the bloom.</summary>
        private readonly struct AddrXorStatePathBloomCallback(BloomFilter bloom, ulong addrKey)
            : IHsstMergeKeyCallback
        {
            public void OnKey(scoped ReadOnlySpan<byte> key)
                => bloom.Add(addrKey ^ PersistedSnapshotBloomBuilder.StatePathKey(key));
        }

    }

    /// <summary>
    /// N-way merge of N persisted snapshots (oldest-first) into <paramref name="writer"/>.
    /// Callers (the compactor in production, the test/benchmark helpers otherwise) own the
    /// source lifecycle: open one reader source per input up front, pass them in here, dispose
    /// after the merge returns. The per-column helpers walk these pre-opened sources and do not
    /// re-open anything inside.
    /// </summary>
    internal static void NWayMergeSnapshots<TWriter, TView, TReader, TPin>(
        ReadOnlySpan<TView> views, ref TWriter writer, BloomFilter bloom)
        where TWriter : IByteBufferWriter
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
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
        using ArrayPoolList<ViewMergeSource<TView, TReader, TPin>> columnSourcesList = new(n, n);
        Span<ViewMergeSource<TView, TReader, TPin>> columnSources = columnSourcesList.AsSpan();

        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            for (int i = 0; i < n; i++)
                columnSources[i] = new(views[i], ResolveColumnBound<TView, TReader, TPin>(views[i], PersistedSnapshotTags.StorageTrieColumnTag));
            NWayMergeStorageTrieColumn<TWriter, TView, TReader, TPin>(columnSources, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StorageTrieColumnTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            for (int i = 0; i < n; i++)
                columnSources[i] = new(views[i], ResolveColumnBound<TView, TReader, TPin>(views[i], PersistedSnapshotTags.StateNodeFallbackTag));
            NWayPackedArrayMerge<TWriter, TView, TReader, TPin>(columnSources, keySize: 33, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StateNodeFallbackTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            for (int i = 0; i < n; i++)
                columnSources[i] = new(views[i], ResolveColumnBound<TView, TReader, TPin>(views[i], PersistedSnapshotTags.StateNodeTag));
            NWayPackedArrayMerge<TWriter, TView, TReader, TPin>(columnSources, keySize: 8, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StateNodeTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            for (int i = 0; i < n; i++)
                columnSources[i] = new(views[i], ResolveColumnBound<TView, TReader, TPin>(views[i], PersistedSnapshotTags.StateTopNodesTag));
            NWayPackedArrayMerge<TWriter, TView, TReader, TPin>(columnSources, keySize: 4, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.StateTopNodesTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            for (int i = 0; i < n; i++)
                columnSources[i] = new(views[i], ResolveColumnBound<TView, TReader, TPin>(views[i], PersistedSnapshotTags.AccountColumnTag));
            NWayMergePerAddressColumn<TWriter, TView, TReader, TPin>(columnSources, ref valueWriter, bloom);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.AccountColumnTag);
        }
        {
            ref TWriter valueWriter = ref outerBuilder.BeginValueWrite();
            NWayMetadataMerge<TWriter, TView, TReader, TPin>(views, ref valueWriter);
            outerBuilder.FinishValueWrite(PersistedSnapshotTags.MetadataTag);
        }

        outerBuilder.Build();
    }

    /// <summary>
    /// N-way streaming merge of a column across N pre-seeded sources into a fixed-key-size
    /// PackedArray HSST. On key collision, newest (highest index) wins. The caller owns
    /// view-seeding and source disposal — pass a <see cref="Span{T}"/> of merge sources whose
    /// bound is the column tag's scope (resolved e.g. via <see cref="ResolveColumnBound"/>).
    /// </summary>
    private static void NWayPackedArrayMerge<TWriter, TView, TReader, TPin>(
        Span<ViewMergeSource<TView, TReader, TPin>> sources, int keySize,
        ref TWriter writer, BloomFilter bloom)
        where TWriter : IByteBufferWriter
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(bloom);
        int n = sources.Length;
        int keyStride = Math.Max(1, keySize);
        using LoserTreeState state = new(n, keyStride);
        using ArrayPoolList<HsstEnumerator<TReader, TPin>> enumeratorsList = new(n, n);
        Span<HsstEnumerator<TReader, TPin>> enumerators = enumeratorsList.AsSpan();
        NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>> cursor =
            new(sources, enumerators, state, keySize);

        HsstPackedArrayMerger.NWayMerge<TWriter, TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>, StatePathBloomCallback>(
            ref writer, NodeRef.Size, ref cursor, new StatePathBloomCallback(bloom));
    }
    /// <summary>
    /// N-way merge of the per-address column (tag 0x01) across N snapshots.
    /// Outer: raw 20-byte Address keys (minSep=4). Every emitted address goes through
    /// <see cref="PerAddressColumnValueMerger{TWriter,TView,TReader,TPin}.MergeValues"/>,
    /// which re-emits per sub-tag (a single matching source is the degenerate case).
    /// Per-address inner sub-tags are 0x00 (account RLP), 0x01 (self-destruct),
    /// 0x02 (slots). Storage-trie nodes live in column 0x05 keyed by addressHash
    /// and are merged separately by <see cref="NWayMergeStorageTrieColumn"/>.
    /// </summary>
    private static void NWayMergePerAddressColumn<TWriter, TView, TReader, TPin>(
        Span<ViewMergeSource<TView, TReader, TPin>> sources, ref TWriter writer, BloomFilter bloom)
        where TWriter : IByteBufferWriter
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        int n = sources.Length;
        // Cache each source's current 20-byte Address key (stride 32 with room).
        const int KeyStride = 32;
        const int AddrKeyLen = PersistedSnapshotTags.AddressKeyLength;
        using LoserTreeState state = new(n, KeyStride);

        // Reusable buffers for the per-address slot prefix/suffix HSST builders, shared across
        // every merged address. The container is a class so the value-merger holds it as a
        // field; amortising rentals matters since the suffix builder runs per prefix group.
        using HsstBTreeBuilderBuffers.Container slotPrefixBuffers = new();
        using ArrayPoolList<HsstEnumerator<TReader, TPin>> enumeratorsList = new(n, n);
        Span<HsstEnumerator<TReader, TPin>> enumerators = enumeratorsList.AsSpan();

        NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>> cursor =
            new(sources, enumerators, state, AddrKeyLen);

        PerAddressColumnValueMerger<TWriter, TView, TReader, TPin> valueMerger =
            new(bloom, slotPrefixBuffers);
        HsstBTreeMerger.NWayMerge<TWriter,
            TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>,
            PerAddressColumnValueMerger<TWriter, TView, TReader, TPin>>(
            ref writer, AddrKeyLen, ref cursor, valueMerger);
    }

    /// <summary>
    /// N-way merge of the storage-trie column (tag 0x05) across N snapshots.
    /// Outer: 20-byte addressHash prefix keys. For each merged addressHash the inner
    /// DenseByteIndex carries sub-tags 0x00 (top), 0x01 (compact), 0x02 (fallback) —
    /// each a nested HSST keyed by encoded TreePath with 6-byte NodeRef values.
    /// Every emitted addressHash goes through a per-addressHash inner rebuild that
    /// re-emits each sub-tag (descending 0x02 → 0x01 → 0x00) via dedicated per-sub-tag
    /// methods on <see cref="StorageTrieColumnValueMerger{TWriter,TView,TReader,TPin}"/>, each
    /// streaming the inner-PackedArray merge for its sub-tag (a single matching source
    /// is the degenerate case).
    /// </summary>
    private static void NWayMergeStorageTrieColumn<TWriter, TView, TReader, TPin>(
        Span<ViewMergeSource<TView, TReader, TPin>> sources, ref TWriter writer, BloomFilter bloom)
        where TWriter : IByteBufferWriter
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        int n = sources.Length;
        const int KeyStride = 32;
        const int AddrKeyLen = PersistedSnapshotTags.AddressHashPrefixLength;
        using LoserTreeState state = new(n, KeyStride);
        using ArrayPoolList<HsstEnumerator<TReader, TPin>> enumeratorsList = new(n, n);
        Span<HsstEnumerator<TReader, TPin>> enumerators = enumeratorsList.AsSpan();
        NWayMergeCursor<TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>> cursor =
            new(sources, enumerators, state, AddrKeyLen);

        StorageTrieColumnValueMerger<TWriter, TView, TReader, TPin> valueMerger = new(bloom);
        HsstBTreeMerger.NWayMerge<TWriter,
            TReader, TPin, ViewMergeSource<TView, TReader, TPin>, TailDispatchEnumeratorFactory<TReader, TPin>,
            StorageTrieColumnValueMerger<TWriter, TView, TReader, TPin>>(
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
    private static void NWayMetadataMerge<TWriter, TView, TReader, TPin>(
        ReadOnlySpan<TView> views, ref TWriter writer)
        where TWriter : IByteBufferWriter
        where TView : IHsstReaderSource<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        int n = views.Length;
        TReader oldestReader = views[0].CreateReader();
        TReader newestReader = views[n - 1].CreateReader();

        // Walk metadata fields directly through the long-aware readers. Each field
        // gets a narrow PinBuffer so the resulting Span is just the field bytes —
        // no wide pin of the entire metadata blob.
        HsstReader<TReader, TPin> oldestRoot = new(in oldestReader, new Bound(0, oldestReader.Length));
        oldestRoot.TrySeek(PersistedSnapshotTags.MetadataTag, out Bound oldestMetaScope);
        HsstReader<TReader, TPin> newestRoot = new(in newestReader, new Bound(0, newestReader.Length));
        newestRoot.TrySeek(PersistedSnapshotTags.MetadataTag, out Bound newestMetaScope);

        Bound fb = SeekField(in oldestReader, oldestMetaScope, PersistedSnapshotTags.MetadataFromBlockKey);
        Bound fh = SeekField(in oldestReader, oldestMetaScope, PersistedSnapshotTags.MetadataFromHashKey);
        Bound tb = SeekField(in newestReader, newestMetaScope, PersistedSnapshotTags.MetadataToBlockKey);
        Bound th = SeekField(in newestReader, newestMetaScope, PersistedSnapshotTags.MetadataToHashKey);
        Bound vb = SeekField(in newestReader, newestMetaScope, PersistedSnapshotTags.MetadataVersionKey);

        using TPin fbPin = oldestReader.PinBuffer(fb);
        using TPin fhPin = oldestReader.PinBuffer(fh);
        using TPin tbPin = newestReader.PinBuffer(tb);
        using TPin thPin = newestReader.PinBuffer(th);
        using TPin vPin = newestReader.PinBuffer(vb);

        static Bound SeekField(scoped in TReader r, Bound scope, scoped ReadOnlySpan<byte> key)
        {
            HsstReader<TReader, TPin> hsst = new(in r, scope);
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
            TReader r = views[i].CreateReader();
            HsstReader<TReader, TPin> root = new(in r, new Bound(0, r.Length));
            if (!root.TrySeek(PersistedSnapshotTags.MetadataTag, out Bound metaScope)) continue;
            HsstReader<TReader, TPin> metaHsst = new(in r, metaScope);
            if (!metaHsst.TrySeek(PersistedSnapshotTags.MetadataRefIdsKey, out Bound rb)
                || rb.Length == 0 || rb.Length % 2 != 0) continue;
            sourceOrigins[i] = rb.Offset;
            totalRefIdsBytes = checked(totalRefIdsBytes + (int)rb.Length);
        }
        sourceStarts[n] = totalRefIdsBytes;

        // Pull every source's ref_ids bytes into one contiguous buffer (sourceBytes), then
        // merge into mergedRefIds. Both share the totalRefIdsBytes upper bound. Heap-rented
        // (not stackalloc) to avoid the >2 GiB risk; in practice this is ~tens of bytes.
        using NativeMemoryListRef<byte> sourceBytesBuf = new(totalRefIdsBytes, totalRefIdsBytes);
        using NativeMemoryListRef<byte> mergedRefIdsBuf = new(totalRefIdsBytes, totalRefIdsBytes);
        Span<byte> sourceBytes = sourceBytesBuf.AsSpan();
        Span<byte> mergedRefIds = mergedRefIdsBuf.AsSpan();
        for (int i = 0; i < n; i++)
        {
            int start = sourceStarts[i];
            int len = sourceStarts[i + 1] - start;
            if (len == 0) continue;
            TReader r = views[i].CreateReader();
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

        using HsstBTreeBuilderBuffers.Container buffers = new();
        using HsstBTreeBuilder<TWriter> builder = new(ref writer, ref buffers.Buffers, PersistedSnapshotTags.MetadataKeyLength);

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
