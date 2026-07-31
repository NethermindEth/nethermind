// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;
using Nethermind.Pbt.Tiles;

namespace Nethermind.Pbt;

/// <summary>
/// Applies EIP-8297 tree-key writes independently below the account, code, and storage partition
/// prefixes, and derives the full state root from the resulting partition roots.
/// </summary>
/// <remarks>
/// The stem trie is the canonical binary trie of the stem set: an internal node exists at every
/// path prefix shared by two or more stems, and each stem node sits at the shortest prefix unique
/// to it — the EIP's minimal-internal-node rule. The batch is applied bulk-set style (mirroring
/// <c>PatriciaTree.BulkSet</c>): the write entries — one per stem, already grouped by the producer —
/// are never globally sorted; instead each group radix-partitions its own range in place into the
/// sixteen boundary slots by the four stem bits at its depth, so a single recursive descent walks
/// every shared prefix only once. A producer that emits its entries already bucketed for the topmost
/// levels — the only ones whose partition touches every entry — hands their bounds over in the batch
/// (<see cref="PbtWriteBatch.Buckets"/>), and those levels skip the work entirely. A range that
/// collapses to a single stem folds that stem's
/// leaf blob and hands the stem node up to be placed at its shortest unique prefix by the bottom-up
/// rebuild of the enclosing groups; hashes are computed on the way back up. Groups and blobs are
/// read/written through <see cref="IPbtStore"/>; untouched child groups are never read or written.
/// <para>
/// A group left with one occupied boundary slot is not stored as a group at all: it is a
/// <see cref="PbtNodeChain"/>, merged with any run below it, and it hoists to the shallowest group
/// that still branches much as a lone stem does — see that type for the canonical form both
/// maintain. Runs are the descent's fast path as well as its compact one: <see cref="ApplyChain"/>
/// jumps straight to the group holding the next branch rather than walking a frame per level.
/// </para>
/// </remarks>
public static partial class TrieUpdater
{
    /// <summary>
    /// Applies <paramref name="changes"/> (each entry a 32-byte tree key → value; an empty value
    /// clears the leaf) to the tree rooted at <paramref name="currentRoot"/>, writing the new leaf
    /// blobs and trie node groups to <paramref name="store"/>, and returns the new root (32 zero
    /// bytes for an empty tree). An empty batch returns <paramref name="currentRoot"/> untouched.
    /// </summary>
    /// <param name="layout">
    /// The tiling the store is written in, and which encoding to write the groups this batch rebuilds
    /// in — and with them the leaf blobs (see <see cref="PbtLeafFormat"/>). The tiling is a property of
    /// the whole store and must not change over one; the encodings may, all folding to the same root
    /// and all being read whatever this says, so a group is converted only by a change that rewrites
    /// it anyway.
    /// </param>
    /// <param name="delta">The change this batch makes to the whole tree's stem count — positive as stems are added, negative as their leaves are zeroed away.</param>
    /// <param name="concurrency">
    /// How many threads may fold the batch: <c>0</c> for the processor count, <c>1</c> to fold on the
    /// calling thread alone. A batch too small to be worth splitting folds on the calling thread
    /// whatever this says.
    /// </param>
    public static PbtPartitionRoots UpdateRoot(
        IPbtStore store, PbtPartitionRoots currentRoots, PbtWriteBatchSet changes, IRefCountingMemoryProvider memoryProvider,
        PbtTrieLayout layout, int concurrency, out PbtSubtreeStats delta)
    {
        PbtPartitionRoot[] partitionRoots = new PbtPartitionRoot[PbtPartitions.Count];
        PbtSubtreeStats[] partitionDeltas = new PbtSubtreeStats[PbtPartitions.Count];
        for (int i = 0; i < PbtPartitions.Count; i++)
        {
            partitionRoots[i] = currentRoots[(PbtPartition)i];
        }

        Parallel.For(0, PbtPartitions.Count, i =>
        {
            PbtPartition partition = (PbtPartition)i;
            PbtWriteBatch batch = changes[partition];
            if (batch.Count == 0) return;

            PbtPartitionRoots updatedRoots = UpdateRoot(
                store, currentRoots, partition, batch, memoryProvider, layout, concurrency, out PbtSubtreeStats partitionDelta);
            partitionRoots[i] = updatedRoots[partition];
            partitionDeltas[i] = partitionDelta;
        });

        delta = default;
        PbtPartitionRoots roots = currentRoots;
        for (int i = 0; i < PbtPartitions.Count; i++)
        {
            roots = roots.With((PbtPartition)i, partitionRoots[i]);
            delta += partitionDeltas[i];
        }

        return roots;
    }

    /// <summary>Applies one partition's disjoint batch and replaces only that partition root.</summary>
    public static PbtPartitionRoots UpdateRoot(
        IPbtStore store, PbtPartitionRoots currentRoots, PbtPartition partition, PbtWriteBatch changes,
        IRefCountingMemoryProvider memoryProvider, PbtTrieLayout layout, int concurrency, out PbtSubtreeStats delta)
    {
        if (changes.Count == 0)
        {
            delta = default;
            return currentRoots;
        }

        PbtGroupFormat groupFormat = layout.GroupFormat();
        PbtPartitionRoot root = layout.Tiling() switch
        {
            PbtTiling.SixLevel => UpdatePartition<PbtSixLevelTileLayout>(store, currentRoots[partition], memoryProvider, groupFormat, changes, concurrency, partition, out delta),
            PbtTiling.EightLevel => UpdatePartition<PbtEightLevelTileLayout>(store, currentRoots[partition], memoryProvider, groupFormat, changes, concurrency, partition, out delta),
            PbtTiling.FourLevel => UpdatePartition<PbtFourLevelTileLayout>(store, currentRoots[partition], memoryProvider, groupFormat, changes, concurrency, partition, out delta),
            PbtTiling.FiveLevel => UpdatePartition<PbtFiveLevelTileLayout>(store, currentRoots[partition], memoryProvider, groupFormat, changes, concurrency, partition, out delta),
            _ => throw new ArgumentOutOfRangeException(nameof(layout)),
        };
        return currentRoots.With(partition, root);
    }

    private static PbtPartitionRoot UpdatePartition<TLayout>(
        IPbtStore store, in PbtPartitionRoot currentRoot, IRefCountingMemoryProvider memoryProvider,
        PbtGroupFormat groupFormat, PbtWriteBatch changes, int concurrency, PbtPartition partition,
        out PbtSubtreeStats delta)
        where TLayout : IPbtTileLayout => partition switch
        {
            PbtPartition.Account or PbtPartition.Code => new Updater<PbtRootedTileLayout<TLayout, PbtDepth4>>(store, memoryProvider, groupFormat, changes, concurrency, partition)
                .Run(currentRoot, changes, out delta),
            PbtPartition.Storage => new Updater<PbtRootedTileLayout<TLayout, PbtDepth1>>(store, memoryProvider, groupFormat, changes, concurrency, partition)
                .Run(currentRoot, changes, out delta),
            _ => throw new ArgumentOutOfRangeException(nameof(partition)),
        };

    /// <summary>
    /// The walk down the tree, as one thread runs it: the frames, the settings they fold by, and the
    /// store writes they buffer.
    /// </summary>
    /// <remarks>
    /// One of these per thread, which is what lets every frame be an instance method with nothing
    /// threaded through it. What the threads have in common each of them simply holds, there being
    /// nothing mutable among it. How a fold gets its threads, and how a frame hands a bucket to one,
    /// is the other half of this class — see <c>TrieUpdater.Parallel.cs</c>.
    /// </remarks>
    /// <remarks>
    /// The frames are <see cref="SkipLocalsInitAttribute"/>: each takes its boundary buffers as a
    /// <see cref="RefList64{T}"/> sized to the tiling's slots, which clears what it hands out, so a
    /// narrow tiling does not pay a prolog zero of the widest one's inline array.
    /// </remarks>
    private sealed partial class Updater<TLayout> where TLayout : IPbtTileLayout
    {
        /// <summary>The largest range handled by the specialized complete-stem sorting network.</summary>
        private const int TinyRange = 3;

        /// <summary>The inclusive range size at which complete-stem sorting replaces radix partitioning.</summary>
        private static int FullSortThreshold => TLayout.BoundarySlots;

        private readonly IPbtStore _store;
        private readonly IRefCountingMemoryProvider _memoryProvider;
        private readonly PbtGroupFormat _writeFormat;
        private readonly TrieNodeKey _rootKey;

        /// <summary>The batch's entries, which the frames permute in place — each job over its own range of them.</summary>
        private readonly PbtWriteBatch.StemEntry[] _entries;

        /// <inheritdoc cref="PbtWriteBatch.Buckets"/>
        private readonly int[]? _buckets;

        /// <summary>The smallest bucket worth handing to another thread; <see cref="int.MaxValue"/> on a fold that stays on the calling thread.</summary>
        private readonly int _minQueueEntries;

        private PbtPartitionRoot Descend(
            in PbtPartitionRoot currentRoot, PbtWriteBatch changes, in Fanout fanout, out PbtSubtreeStats delta)
        {
            using RefCountingMemory? rootData = _store.GetTrieNode(_rootKey, currentRoot.Hash);
            BufferWriter writer = new(_memoryProvider);
            NodeResult root;
            bool changed;
            try
            {
                ApplyGroup(
                    _rootKey, changes.Entries, TreeReader<TLayout>.Of(rootData), currentRoot.Hash,
                    new BucketPlan(changes.Buckets, TLayout.RootDepth, isSorted: false), fanout, ref writer, out root, out changed, out delta);
                if (writer.Detach() is { } folded) root = root.WithBlob(folded);
            }
            finally
            {
                writer.Dispose();
            }

            using (root)
            {
                if (changed && root.Blob is { } rootBlob)
                {
                    rootBlob.AcquireLease();
                    _store.SetTrieNode(_rootKey, root.NodeHash(), rootBlob);
                }
                else if (changed && root.Kind == NodeKind.Absent && rootData is not null)
                {
                    _store.SetTrieNode(_rootKey, currentRoot.Hash, null);
                }

                return new PbtPartitionRoot(root.Kind, root.NodeHash());
            }
        }

        /// <summary>
        /// Applies <paramref name="entries"/> — a non-empty range of one-per-stem writes sharing bits
        /// <c>[0, key.Depth)</c> in any order — to the stored group at <paramref name="key"/>, whose
        /// current content is <paramref name="existingData"/> (<c>null</c> only at the root, when the
        /// tree has no stored root group yet). Writes or removes every affected group blob at or below
        /// <paramref name="key"/> and settles the node now occupying the group's root position into
        /// <paramref name="result"/> for the parent's boundary slot. A stem result is not written here: it
        /// hoists into the parent, cascading across group boundaries (except at the root group, whose root
        /// may hold a stem). The caller disposes the result.
        /// </summary>
        /// <param name="existingData">
        /// The group's stored blob, which the caller owns and this keeps no lease on beyond the
        /// call: the occupants read out of it take their own.
        /// </param>
        /// <param name="beforeHash"><inheritdoc cref="RebuildNode" path="/param[@name='beforeHash']"/></param>
        /// <param name="plan"><inheritdoc cref="ResolveBoundaries" path="/param[@name='plan']"/></param>
        /// <param name="fanout">
        /// Where this frame hands the buckets it is not folding itself. Carried down the descent rather
        /// than held, since the queue belongs to whichever thread is running the fold, not to the updater.
        /// </param>
        /// <param name="writer">The buffer dedicated to this group's encoding.</param>
        /// <param name="result">The node now occupying the group's root position, written straight into the caller's slot.</param>
        /// <param name="changed"><inheritdoc cref="RebuildNode" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="RebuildNode" path="/param[@name='delta']"/></param>
        [SkipLocalsInit]
        private void ApplyGroup(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, TreeReader<TLayout> existingData,
            in ValueHash256 beforeHash, scoped BucketPlan plan, in Fanout fanout, ref BufferWriter writer,
            out NodeResult result, out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty && TLayout.IsGroupDepth(depth));
            Debug.Assert(depth <= TLayout.MaxGroupDepth);

            TreeReader<TLayout> occupants = existingData.AsGroup();
            PbtTrieNodeGroup<TLayout> existing = occupants.Group();

            using PbtLeasedFrameBuffer<NodeResult> resultBuffer = new(TLayout.BoundarySlots);
            Span<NodeResult> results = resultBuffer.Span;

            GroupShape shape = ResolveBoundaries(key, entries, occupants, existing, existing.BoundaryShape(), plan, fanout, results);
            result = RebuildNode(
                key, occupants, existing, results, shape, beforeHash, existing.Stats, ref writer,
                out changed, out delta);
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to a subtree with no stored group at <paramref name="key"/>:
        /// either empty, or holding a single stem <paramref name="pushed"/> down from the parent's
        /// boundary slot. A range holding one uncontended stem has reached the shortest prefix no other
        /// stem shares, so that stem's node is built here, out of its leaf blob, without descending;
        /// otherwise the pushed stem and the writes are routed on down together.
        /// </summary>
        /// <param name="plan"><inheritdoc cref="ResolveBoundaries" path="/param[@name='plan']"/></param>
        /// <param name="fanout"><inheritdoc cref="ApplyGroup" path="/param[@name='fanout']"/></param>
        /// <param name="writer"><inheritdoc cref="ApplyGroup" path="/param[@name='writer']"/></param>
        /// <param name="result"><inheritdoc cref="ApplyGroup" path="/param[@name='result']"/></param>
        /// <param name="changed"><inheritdoc cref="RebuildNode" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="RebuildNode" path="/param[@name='delta']"/></param>
        [SkipLocalsInit]
        private void ApplyPushedStem(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in TreeReader<TLayout> pushedReader,
            scoped BucketPlan plan, in Fanout fanout, ref BufferWriter writer, out NodeResult result,
            out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            Occupant pushed = pushedReader.Occupant;
            Debug.Assert(!entries.IsEmpty && TLayout.IsGroupDepth(depth));
            // a chain routes to ApplyChain instead: it is a whole subtree, not a node to place, and the
            // collapse below would drop it
            Debug.Assert(pushed.Kind is NodeKind.Absent or NodeKind.Stem);

            // Whatever was pushed down is the whole of what is here, so a lone stem is the only stem
            // this frame's subtree can hold before the writes.
            PbtSubtreeStats beforeStats = pushed.Kind == NodeKind.Stem ? PbtSubtreeStats.OneStem : default;

            // A lone write whose stem nothing else contends — either nothing was pushed down here (an
            // empty subtree, or this very stem relocating in) or what was pushed is that same stem,
            // updating in place. Its writes go into its leaf blob, that blob merkelizes to a subtree
            // root, and stem + root is the whole of its stem node: it is built here and handed up for
            // an enclosing rebuild to place at its shortest unique prefix, so nothing below needs
            // walking. A *different* pushed stem does contend — it diverges from this write somewhere
            // further down and would be dropped if the node were built here — so that case, and any
            // batch of several writes, routes deeper instead. This also serves depth 248, where every
            // remaining range is necessarily a single stem.
            Stem stem = entries[0].Stem;
            if (entries.Length == 1 && (pushed.Kind == NodeKind.Absent || pushed.Stem == stem))
            {
                // a blob left with no leaves deletes its stem, so the node goes absent rather than up
                ValueHash256 priorSubtreeRoot = pushed.Kind == NodeKind.Stem ? pushed.Hash : default;
                bool isEmpty = ComputeBlob(stem, entries[0].Changes, pushed.Kind == NodeKind.Absent, priorSubtreeRoot, out ValueHash256 subtreeRoot);
                result = isEmpty ? default : NodeResult.StemNode(stem, subtreeRoot);
                changed = result.NodeHash() != pushed.NodeHash();

                // The one place a stem is born or dies, so the one place a delta starts: this range
                // held a stem or it did not, and it holds one now or it does not.
                delta = (isEmpty ? default : PbtSubtreeStats.OneStem) - beforeStats;
                return;
            }

            // Every entry in the range shares bits [0, depth) with `stem`, so at the stem's full depth
            // they are all this same stem, and the range not having collapsed above means it holds more
            // than one entry for it: the producer failed to merge its writes to the stem, which
            // PbtWriteBatch requires of it. Nothing is left to partition on, so this must not descend.
            if (depth >= Stem.LengthInBits) throw new InvalidOperationException($"Stem {stem} written more than once in a single batch");

            Debug.Assert(depth <= TLayout.MaxGroupDepth);

            // Nothing is stored at this key or below it — a stem node lives in its parent group's encoding
            // and its subtree in a leaf blob keyed by the stem — so the groups from here down to wherever
            // the writes part hold a single child apiece and no blob (invariant 3). Walking them a frame
            // per four levels only for each rebuild to collapse one into a run and the frame outside it to
            // absorb that run costs a fold over the whole remaining path every time, which is quadratic in
            // the run's length; the jump below folds once instead, exactly as ApplyChain does for a run
            // already stored. A precalculated level describes one range at one depth and skipping levels
            // leaves it describing neither, so a frame holding one stays on the level-by-level path — no
            // loss, as runs start far below the depths a producer buckets. A frame that inherited where the
            // entries part skips even this one partition, what an ancestor found holding for this range
            // too; the level then stays unbucketed for the resolve below to fill from that same depth,
            // should the jump not fire.
            scoped ReadOnlySpan<int> buckets = plan.Precalculated;
            Span<byte> buffer = stackalloc byte[plan.GetBufferSize(entries.Length, depth)];
            int entriesBranch = plan.BranchDepth;
            bool isSorted = plan.IsSorted;
            int branchDepth = depth;
            if (buckets.IsEmpty)
            {
                if (entriesBranch <= depth)
                {
                    PartitionOutcome outcome = Partition(entries, depth, buffer);
                    entriesBranch = outcome.BranchDepth;
                    isSorted = outcome.IsSorted;
                    buckets = outcome.Level;
                }

                branchDepth = entriesBranch;
            }

            AssertBranchDepthSound(entries, depth, entriesBranch);

            // Past here `branchDepth` is the whole subtree's, narrowed by the pushed stem — what bounds the
            // jump — while `entriesBranch` stays the entries' alone, which is what buckets them.
            if (branchDepth > depth && pushed.Kind == NodeKind.Stem)
            {
                // The pushed stem is as much of this subtree as the writes are, so it bounds the run too.
                int diff = pushed.Stem.FirstDifferingBit(stem, depth);
                int pushedBranch = (uint)diff < Stem.LengthInBits ? TLayout.GroupDepthOf(diff) : Stem.LengthInBits;
                if (pushedBranch < branchDepth) branchDepth = pushedBranch;
            }

            // Stems that never part leave the branch past the trie's last group, which no key names: that
            // is the duplicate-stem batch, left to the descent to reject where it already does.
            if (depth > TLayout.RootDepth && branchDepth > depth && branchDepth <= TLayout.MaxGroupDepth)
            {
                Debug.Assert(depth > TLayout.RootDepth, "a run starts past the partition root (invariant 4)");
                TrieNodeKey branchKey = TrieNodeKey.For(branchDepth, stem);

                // The same frame again at the group the run ends in, which is the ordinary descent from
                // there down: the jump cannot fire a second time, whatever bounded it — the entries or the
                // pushed stem — parting at that very depth. The pushed occupant rides along to be placed
                // there rather than here, and the entries' own branch depth with it, which still stands
                // where the pushed stem is what cut the run short. A run is about to be minted above that
                // group, so it is one no blob of this frame's can hold: it owns its own.
                BufferWriter branchWriter = new(_memoryProvider);
                NodeResult inner;
                try
                {
                    ApplyPushedStem(
                        branchKey, entries, pushedReader, new BucketPlan(default, entriesBranch, isSorted), fanout, ref branchWriter,
                        out inner, out _, out delta);
                    if (branchWriter.Detach() is { } innerBlob) inner = inner.WithBlob(innerBlob);
                }
                finally
                {
                    branchWriter.Dispose();
                }

                // A run's node hash is its path's, unlike a stem's, so the inner frame's answer to whether
                // it changed is about a node starting at the wrong depth. It is settled here instead, once
                // the run reaching down from this frame is what the pushed occupant is compared against.
                result = WrapIntoChain(depth, branchKey, inner, innerBlobStored: false, beforeStats + delta);
                changed = result.NodeHash() != pushed.NodeHash();
                return;
            }

            int seedSlot = pushed.Kind == NodeKind.Stem ? TLayout.SlotOf(pushed.Stem, depth) : -1;

            // a pushed stem roots no blob of its own: its subtree is its leaf blob, keyed by the stem
            TreeReader<TLayout> occupants = pushedReader.WithSeed(seedSlot);
            using PbtLeasedFrameBuffer<NodeResult> resultBuffer = new(TLayout.BoundarySlots);
            Span<NodeResult> results = resultBuffer.Span;

            // `buckets` carries the level partitioned above where there was none to start with, and
            // `entriesBranch` lets the resolve fill one itself where nothing was partitioned at all, so the
            // range is bucketed once however this frame reached here.
            BucketPlan resolvePlan = new(buckets, entriesBranch, isSorted);
            GroupShape shape = ResolveBoundaries(
                key, entries, occupants, default, occupants.BoundaryShape(), resolvePlan, fanout, results);
            MergeUntouchedSeed(occupants, results, shape.Touched);

            result = RebuildNode(
                key, occupants, default, results, shape, pushed.NodeHash(), beforeStats, ref writer,
                out changed, out delta);
        }

        /// <summary>
        /// The group's boundary shape once its touched slots are resolved: the masks and the stat delta the
        /// rebuild reads.
        /// </summary>
        /// <remarks>
        /// Covers every slot, not just the descended ones: each boundary resolver is handed the shape the
        /// frame started with and folds the slots it never visited back in as it builds this,
        /// so <see cref="RebuildNode"/> — which needs them all — cannot be handed a half-built one.
        /// </remarks>
        private readonly record struct GroupShape(
            BoundarySlotMasks<TLayout> Boundary, SlotBitmask<TLayout> Changed, SlotBitmask<TLayout> Touched,
            SlotBitmask<TLayout> StoredChildren, PbtSubtreeStats Deltas);

        /// <summary>
        /// Descends only the non-empty buckets of the group at <paramref name="key"/>, applying each touched
        /// slot's writes to its child and settling the result into <paramref name="results"/>. An untouched
        /// slot is left alone entirely — it keeps its occupant, which the settle reads on demand. This is
        /// for a frame whose children are stored under their own keys, each folding into a buffer of its own
        /// that this frame plants or removes.
        /// </summary>
        /// <remarks>
        /// A bucket big enough to be worth the hand-off (<see cref="_minQueueEntries"/>) is handed to
        /// whichever thread reaches it first; everything else is folded here and now.
        /// </remarks>
        /// <returns>
        /// The whole frame's shape, which <see cref="RebuildNode"/> needs: the slots this descended, and
        /// <paramref name="untouched"/>'s over the rest.
        /// </returns>
        /// <param name="occupants">
        /// The frame's boundary occupants, read on demand from a stored group encoding or from the one
        /// node a run-split or pushed stem seeds.
        /// </param>
        /// <param name="existing">The already decoded stored group; default for a seeded frame with no stored group.</param>
        /// <param name="untouched">
        /// The boundary the frame started with, from which the slots no write reaches carry over: a stored
        /// group's own shape, or the lone occupant a run-split or pushed stem seeds.
        /// </param>
        /// <param name="plan"><inheritdoc cref="BucketPlan" path="/summary"/></param>
        /// <param name="fanout"><inheritdoc cref="ApplyGroup" path="/param[@name='fanout']"/></param>
        private GroupShape ResolveBoundaries(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in TreeReader<TLayout> occupants,
            in PbtTrieNodeGroup<TLayout> existing, in BoundarySlotMasks<TLayout> untouched,
            scoped BucketPlan plan, in Fanout fanout, Span<NodeResult> results)
        {
            int depth = key.Depth;
            AssertBranchDepthSound(entries, depth, plan.BranchDepth);

            Span<byte> buffer = stackalloc byte[plan.GetBufferSize(entries.Length, depth)];
            scoped PartitionOutcome outcome = plan.BucketSort(entries, depth, buffer);

            AssertCompactLevel(outcome, entries.Length);

            PbtWriteBatch.BucketLevel<TLayout> buckets = outcome.IterateBuckets();
            SlotBitmask<TLayout> touched = buckets.Touched;
            PartitionResult partition = new(plan, outcome.BranchDepth, outcome.IsSorted);
            SlotBitmask<TLayout> storedChildren = default;
            BoundaryScan scan = default;

            // The buckets this frame handed out, which the settle below takes back whatever it can
            // still do itself.
            QueuedBuckets queued = default;

            try
            {
                foreach ((int slot, Range range) in buckets)
                {
                    TreeReader<TLayout> reader = occupants.Reader(slot, existing);
                    Span<PbtWriteBatch.StemEntry> bucket = entries[range];
                    TrieNodeKey childKey = key.ChildGroup(slot, TLayout.LevelsPerGroup);
                    BucketPlan childPlan = partition.GetChildPlan(slot);

                    if (MayQueue(touched, in fanout) && bucket.Length >= _minQueueEntries &&
                        TryQueue(slot, childKey, bucket, reader, childPlan, in fanout, ref queued))
                    {
                        continue;
                    }

                    ref NodeResult result = ref results[slot];
                    ApplyKeyedChild(childKey, bucket, reader, childPlan, fanout, out result, out bool childChanged, out PbtSubtreeStats childDelta, out bool storedChild);
                    if (storedChild) storedChildren.Set(slot);
                    scan.Add(slot, result, childChanged, childDelta);
                }
            }
            catch
            {
                // A frame this thread has given up on still has buckets being folded on other threads,
                // reading the entries and writing the store as they go. They are seen through here rather
                // than left to race the unwinding fold, and what they produced is released with them.
                if (!queued.IsEmpty) Discard(in fanout, ref queued);
                throw;
            }

            if (!queued.IsEmpty) Settle(in fanout, ref queued, results, ref scan, ref storedChildren);

            return scan.ToShape(touched, storedChildren, untouched);
        }

        /// <summary>
        /// Applies <paramref name="bucket"/> to the child group at <paramref name="childKey"/>, whose
        /// boundary slot holds <paramref name="occupant"/>, and settles the node now at that key into
        /// <paramref name="result"/> — with the encoding to plant, where the fold produced one.
        /// </summary>
        /// <remarks>
        /// One frame of the descent, and the unit of work another thread may take over: everything it
        /// touches is under <paramref name="childKey"/>, bar the removal of that key itself, which no
        /// other range can reach either.
        /// </remarks>
        /// <param name="fanout"><inheritdoc cref="ApplyGroup" path="/param[@name='fanout']"/></param>
        /// <param name="storedChild">Whether the store held a blob at <paramref name="childKey"/>, which the parent's rebuild needs to know to settle a collapse onto it.</param>
        private void ApplyKeyedChild(
            in TrieNodeKey childKey, Span<PbtWriteBatch.StemEntry> bucket, in TreeReader<TLayout> reader,
            scoped BucketPlan childPlan, in Fanout fanout, out NodeResult result, out bool changed,
            out PbtSubtreeStats delta, out bool storedChild)
        {
            BufferWriter owned = new(_memoryProvider);
            Occupant occupant = reader.Occupant;
            storedChild = false;
            try
            {
                if (occupant.Kind == NodeKind.Internal)
                {
                    // a stored child group descends with its own content; the boundary internal caches its
                    // old root hash, which the child no longer stores itself
                    using RefCountingMemory? childData = _store.GetTrieNode(childKey, occupant.NodeHash());
                    storedChild = childData is not null;
                    ApplyGroup(childKey, bucket, TreeReader<TLayout>.Of(childData), occupant.NodeHash(), childPlan, fanout, ref owned, out result, out changed, out delta);
                    if (owned.Detach() is { } childBlob) result = result.WithBlob(childBlob);

                    // No frame writes its own key; the parent settles each child's: a stored one the writes
                    // emptied to nothing, or collapsed into a run the parent now holds, is removed here (a
                    // child that produced a blob of its own is planted by the parent's rebuild).
                    if (storedChild && changed && result.KeyedBlob is null) _store.SetTrieNode(childKey, occupant.NodeHash(), null);
                    return;
                }

                if (occupant.Kind == NodeKind.Chain)
                {
                    ApplyChain(childKey, bucket, reader, childPlan, fanout, ref owned, out result, out changed, out delta);
                }
                else
                {
                    ApplyPushedStem(childKey, bucket, reader, childPlan, fanout, ref owned, out result, out changed, out delta);
                }

                if (owned.Detach() is { } blob) result = result.WithBlob(blob);
            }
            finally
            {
                owned.Dispose();
            }
        }

        /// <summary>What the boundary walk adds up as it settles each slot, for the shape it hands the rebuild.</summary>
        private struct BoundaryScan
        {
            private SlotBitmask<TLayout> _occupied;
            private SlotBitmask<TLayout> _stems;
            private SlotBitmask<TLayout> _chains;
            private SlotBitmask<TLayout> _changed;
            private PbtSubtreeStats _deltas;

            public void Add(int slot, in NodeResult result, bool changed, in PbtSubtreeStats delta)
            {
                Debug.Assert(changed || delta.IsZero, "an unchanged subtree is the same subtree, so it holds the same stems");

                if (changed) _changed.Set(slot);
                if (result.Kind != NodeKind.Absent) _occupied.Set(slot);
                if (result.Kind == NodeKind.Stem) _stems.Set(slot);
                if (result.Kind == NodeKind.Chain) _chains.Set(slot);
                _deltas += delta;
            }

            /// <summary>
            /// The walk's own slots, with the slots it never visited folded back in from
            /// <paramref name="untouched"/> — the shape the frame started with.
            /// </summary>
            /// <remarks>
            /// The walk only ever settles a touched slot, so the two halves are disjoint by construction
            /// and the mask below is what keeps them so however a caller names its starting shape.
            /// </remarks>
            public readonly GroupShape ToShape(
                SlotBitmask<TLayout> touched, SlotBitmask<TLayout> storedChildren, in BoundarySlotMasks<TLayout> untouched) =>
                new(
                    new BoundarySlotMasks<TLayout>(
                        _occupied | untouched.Presence.Except(touched),
                        _stems | untouched.Stems.Except(touched),
                        _chains | untouched.Chains.Except(touched)),
                    _changed, touched, storedChildren, _deltas);
        }

        /// <summary>
        /// Settles this frame's node from the boundary <paramref name="results"/> and their
        /// <paramref name="shape"/> that its boundary resolver produced — collapse to a run, hoist
        /// a stem, skip an unchanged group, or fold a fresh one — and persists each child's blob, returning
        /// the node now occupying this frame's key. An untouched slot is read back from
        /// <paramref name="occupants"/> on demand, bar what a seeded caller wrote into
        /// <paramref name="results"/> before the call because nothing can read it back later.
        /// </summary>
        /// <remarks>
        /// One call per group frame, from each of the three that resolve one:
        /// <list type="bullet">
        /// <item><see cref="ApplyGroup"/> — a stored group.</item>
        /// <item><see cref="ApplyPushedStem"/> — a frame holding at most a stem pushed down from the parent.</item>
        /// <item><see cref="ApplyChainSplit"/> — the group a run branches into, seeding what is left of the run.</item>
        /// </list>
        /// The last two seed their lone occupant into a frame with no stored group behind it; the reader
        /// carries that occupant into <paramref name="results"/> when no write touches its slot.
        /// </remarks>
        /// <param name="occupants"><inheritdoc cref="ResolveBoundaries" path="/param[@name='occupants']"/></param>
        /// <param name="existing"><inheritdoc cref="ResolveBoundaries" path="/param[@name='existing']"/></param>
        /// <param name="beforeHash">The hash the root position contributed before, against which <paramref name="changed"/> is decided.</param>
        /// <param name="beforeStats">
        /// What this frame's subtree amounted to before the writes, which <paramref name="delta"/> is
        /// measured from and which the rebuilt group's own statistics are hoisted onto.
        /// </param>
        /// <param name="changed">
        /// Set to <c>false</c> when the writes leave this group's root node identical to <paramref name="beforeHash"/>
        /// (all writes were no-ops), letting the parent reuse its cached hash and skip its own rewrite;
        /// <c>true</c> otherwise.
        /// </param>
        /// <param name="delta">
        /// How the writes changed this frame's subtree, for the parent to hoist onto its own statistics.
        /// It measures the subtree's content, not the blobs holding it, so collapsing to a run or hoisting
        /// a stem out leaves it alone. A slot no write touches contributes nothing, which is what lets the
        /// descent leave that child unread — an absolute count could not.
        /// </param>
        /// <param name="writer"><inheritdoc cref="ApplyGroup" path="/param[@name='writer']"/></param>
        [SkipLocalsInit]
        private NodeResult RebuildNode(
            in TrieNodeKey key, in TreeReader<TLayout> occupants, in PbtTrieNodeGroup<TLayout> existing,
            Span<NodeResult> results, in GroupShape shape, in ValueHash256 beforeHash,
            in PbtSubtreeStats beforeStats, ref BufferWriter writer,
            out bool changed, out PbtSubtreeStats delta)
        {
            bool isRoot = key.Depth == TLayout.RootDepth;
            BoundarySlotMasks<TLayout> boundary = shape.Boundary;
            (SlotBitmask<TLayout> occupied, SlotBitmask<TLayout> stems, SlotBitmask<TLayout> chains) = boundary;
            SlotBitmask<TLayout> changedSlots = shape.Changed;
            AssertUntouchedMerged(occupants, existing, boundary, shape.Touched);
            NodeKind rootKind = boundary.RootKind;

            // Every stem of this subtree sits under one of the sixteen slots, so what they hoisted is the
            // whole of what changed here. This holds however the nodes are settled below: the statistics
            // count the subtree's content, and none of removing, collapsing or rebuilding moves a stem.
            delta = shape.Deltas;
            PbtSubtreeStats afterStats = beforeStats + shape.Deltas;

            // A group left with one internal child is a run of single-child levels: five nodes that all
            // change whenever its one leaf does, and that nothing reads on their own. It stores as a
            // PbtNodeChain instead, merged with any run below so that a run is always as long as it can be.
            // The root group is exempt, as it is for a lone stem: it has no parent to hoist into.
            if (rootKind == NodeKind.Internal && !isRoot && occupied.HoldsOne)
            {
                int survivorSlot = occupied.First;
                Debug.Assert(
                    occupants.HasStoredEncoding || results[survivorSlot].Kind != NodeKind.Absent,
                    "a seeded frame's occupant rides in results, there being no encoding to read it back out of");

                // an untouched survivor was never copied into results — read it straight from the occupant
                NodeResult survivor = results[survivorSlot].Kind != NodeKind.Absent
                    ? results[survivorSlot].Lease()
                    : AdoptOccupant(occupants.Reader(survivorSlot, existing).Occupant);

                writer.Reset(0);

                // Nothing is read to settle this: a survivor that is itself a run was in this very group's
                // encoding, so merging with it — which is what keeps runs maximal — needs no lookup,
                // however untouched it is. The fold to this frame's depth happens before the comparison:
                // unlike a stem's, a run's node hash is its path's, so it is only the same node once it
                // starts where beforeHash did.
                NodeResult chain = WrapIntoChain(
                    key.Depth, key.ChildGroup(survivorSlot, TLayout.LevelsPerGroup), survivor, shape.StoredChildren[survivorSlot], afterStats);
                changed = chain.NodeHash() != beforeHash;
                Release(results, handedUp: -1);
                return chain;
            }

            // A group that folds to nothing, or to a lone stem hoisting into the parent, encodes to
            // nothing: there is no blob to rebuild, and its key — now holding no group — is one the parent
            // removes. The hoisted result is handed straight up, taking its lease with it.
            // Only deletes get here, by emptying a frame or leaving it a single stem: ApplyGroup for a
            // stored group — the root included, whose empty result clears the root key — and
            // ApplyPushedStem for a frame that holds no group of its own.
            if (rootKind != NodeKind.Internal && !(isRoot && rootKind == NodeKind.Stem))
            {
                int hoistedSlot = rootKind == NodeKind.Absent ? -1 : occupied.First;
                NodeResult hoisted = hoistedSlot < 0 ? default
                    : results[hoistedSlot].Kind != NodeKind.Absent ? results[hoistedSlot] : PromoteOccupant(occupants.Reader(hoistedSlot, existing).Occupant);
                changed = hoisted.NodeHash() != beforeHash;
                Release(results, hoistedSlot);
                writer.Reset(0);
                return hoisted;
            }

            // Hand the fold only the boundaries it cannot read back out of the existing encoding, in slot
            // order: the changed ones, and — when there is no existing group to read from (a run split or a
            // pushed stem seeds its occupants into an empty one) — every occupied slot. A changed slot that
            // emptied out contributes no boundary, so only the occupied ones go in.
            using PbtFrameBuffer<(int Slot, Boundary Node)> boundaryBuffer = new(TLayout.BoundarySlots);
            Span<(int Slot, Boundary Node)> changedBoundaries = boundaryBuffer.Span;
            int changedCount = 0;
            // ascending, as the fold requires
            foreach (int slot in occupants.HasStoredEncoding ? occupied & changedSlots : occupied)
            {
                Debug.Assert(
                    occupants.HasStoredEncoding || results[slot].Kind != NodeKind.Absent,
                    "a seeded frame has no encoding to read a boundary back out of, so every occupiedBitmask slot must ride in results");

                NodeResult node = results[slot];
                changedBoundaries[changedCount++] = (slot, new Boundary(
                    node.Hash,
                    stems[slot] ? node.Stem : default,
                    chains[slot] ? node.Blob : null));
            }

            GroupRebuild<TLayout> rebuild = new(changedBoundaries[..changedCount], existing, boundary, changedSlots, beforeHash, _writeFormat);
            (Stem? rootStem, ValueHash256 rootHash) = rebuild.Rebuild(ref writer, afterStats);
            NodeResult rootNode = rootStem is { } stem ? NodeResult.StemNode(stem, rootHash) : NodeResult.Internal(rootHash);

            // Unchanged root => the encoding is byte-identical to what is stored (an internal root whose
            // hash matches implies the same subtree, hence the same cached boundary hashes).
            changed = rootNode.NodeHash() != beforeHash;

            // Plant each rebuilt child group at its independent key. A run is held by this group's
            // encoding instead and has no keyed blob.
            foreach (int slot in changedSlots)
            {
                if (results[slot].KeyedBlob is { } childBlob)
                {
                    childBlob.AcquireLease();
                    _store.SetTrieNode(key.ChildGroup(slot, TLayout.LevelsPerGroup), results[slot].NodeHash(), childBlob);
                }
            }

            Release(results, handedUp: -1);

            return rootNode;
        }

        /// <summary>
        /// Promotes an untouched occupant to a result the frame can hand up, copying a run out of the
        /// encoding it sits in — which the frame reading it is about to replace — into memory of its own.
        /// </summary>
        private NodeResult AdoptOccupant(in Occupant occupant) =>
            occupant.Kind == NodeKind.Chain ? CopyChainNode(occupant.ChainData) : PromoteOccupant(occupant);

        private static NodeResult PromoteOccupant(in Occupant occupant) => occupant.Kind switch
        {
            NodeKind.Internal => NodeResult.Internal(occupant.Hash),
            NodeKind.Stem => NodeResult.StemNode(occupant.Stem, occupant.Hash),
            _ => default,
        };

        private static void MergeUntouchedSeed(
            in TreeReader<TLayout> occupants, Span<NodeResult> results, SlotBitmask<TLayout> touched)
        {
            int slot = occupants.SeedSlot;
            if (slot < 0 || touched[slot]) return;

            Occupant seed = occupants.Occupant;
            results[slot] = seed.Kind == NodeKind.Chain
                ? NodeResult.Chain(occupants.Memory!).Lease()
                : PromoteOccupant(seed);
        }

        /// <summary>
        /// Releases the leases <paramref name="nodes"/> hold once a frame is done with them, bar slot
        /// <paramref name="handedUp"/> — the node it returns, whose lease goes with it (<c>-1</c> when
        /// it returns a node of its own).
        /// </summary>
        /// <remarks>
        /// Every slot is left <c>default</c>, the handed-up one included: the caller took its value
        /// before this ran, so what stays behind holds no lease and
        /// <see cref="PbtLeasedFrameBuffer{T}"/> has nothing left to release should the frame be
        /// abandoned after this point.
        /// </remarks>
        private static void Release<T>(Span<T> nodes, int handedUp) where T : struct, IDisposable
        {
            for (int slot = 0; slot < nodes.Length; slot++)
            {
                if (slot != handedUp) nodes[slot].Dispose();
                nodes[slot] = default;
            }
        }

        /// <summary>
        /// The result of applying a batch to a slot: the node now occupying it, handed up for a parent to
        /// place. Usually backed by an encoding, but the folded internal group root has no entry, so it is
        /// carried by value instead — see <see cref="Unstored"/>.
        /// </summary>
        /// <remarks>
        /// The descent's output, distinct from its <see cref="Occupant"/> input. A group's encoding is not
        /// among what it carries: the frame folded that into a dedicated <see cref="BufferWriter"/>, whose
        /// buffer <see cref="BufferWriter.Detach"/> hands over for the parent to plant. What a result does
        /// hold is a node with nowhere else to live — a run whose
        /// <see cref="PbtNodeChain"/> encoding no group holds until an ancestor writes it into its own —
        /// and the lease keeps it safe to
        /// hand up: it outlives the buffer's other owners, whether a <c>using</c> in the frame it leaves or
        /// an <see cref="IPbtStore"/> write taking the encoding with it. A group's internal root stores no
        /// entry at all (the parent caches it in its boundary slot), so it carries its hash by value, as
        /// <see cref="FoldedNode.Unstored"/> does for a skipped odd level one frame down. An absent result
        /// holds nothing, so <c>default</c> is one.
        /// </remarks>
        internal readonly struct NodeResult : IDisposable
        {
            private readonly NodeKind _kind;
            private readonly ValueHash256 _hash;
            private readonly Stem _stem;
            private readonly RefCountingMemory? _blob;

            private NodeResult(NodeKind kind, in ValueHash256 hash, in Stem stem, RefCountingMemory? blob)
            {
                _kind = kind;
                _hash = hash;
                _stem = stem;
                _blob = blob;
            }

            /// <summary>An internal node folding to <paramref name="hash"/>, with a rebuilt group <paramref name="blob"/> to persist at its key when one changed.</summary>
            public static NodeResult Internal(in ValueHash256 hash, RefCountingMemory? blob = null) => new(NodeKind.Internal, hash, default, blob);

            /// <summary>A stem node; <paramref name="blob"/> is the root group's encoding when its lone root stem sits in one, else <c>null</c> (a hoisting stem has no blob of its own).</summary>
            public static NodeResult StemNode(in Stem stem, in ValueHash256 subtreeRoot, RefCountingMemory? blob = null) => new(NodeKind.Stem, subtreeRoot, stem, blob);

            /// <summary>A run, whose <paramref name="blob"/> is its <see cref="PbtNodeChain"/> encoding for an ancestor to hold or absorb.</summary>
            public static NodeResult Chain(RefCountingMemory blob) => new(NodeKind.Chain, PbtNodeChain.NodeHashOf(blob.GetSpan()), default, blob);

            /// <summary>
            /// The same node, now carrying the encoding <paramref name="blob"/> a frame folded into a
            /// buffer of its own, for the parent to plant under this node's key.
            /// </summary>
            public NodeResult WithBlob(RefCountingMemory blob)
            {
                Debug.Assert(_blob is null, "a node that already came with an encoding is no frame's own");
                return new NodeResult(_kind, _hash, _stem, blob);
            }

            public NodeKind Kind => _kind;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Hash"/>
            public ValueHash256 Hash => _hash;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Stem"/>
            public Stem Stem => _stem;

            /// <summary>
            /// The encoding this node came with — a rebuilt group, or a run's own — or <c>null</c> when it
            /// has none (an unchanged group, a hoisting stem, an absent subtree).
            /// </summary>
            public RefCountingMemory? Blob => _blob;

            /// <summary>
            /// The encoding to persist at this node's key, which a run has none of: it is no blob of the
            /// store's, but an entry of the group above it, so its key must hold nothing.
            /// </summary>
            public RefCountingMemory? KeyedBlob => _kind == NodeKind.Chain ? null : _blob;

            /// <summary>A run's encoding, borrowed from the memory this result leases.</summary>
            public ReadOnlySpan<byte> ChainData
            {
                get
                {
                    Debug.Assert(_kind == NodeKind.Chain, "only a run is an encoding all of its own");
                    return _blob!.GetSpan();
                }
            }

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.NodeHash"/>
            public ValueHash256 NodeHash() => _kind == NodeKind.Stem ? StemLeafBlob.ComputeStemNodeHash(_stem, _hash) : _hash;

            /// <summary>Takes a second lease on the blob, for another owner to release in its own time.</summary>
            public NodeResult Lease()
            {
                _blob?.AcquireLease();
                return this;
            }

            public void Dispose() => ((IDisposable?)_blob)?.Dispose();
        }

        /// <summary>The leaf blob layout paired with the group format; every-three groups deliberately keep interleaved leaves.</summary>
        private PbtLeafFormat LeafFormat => _writeFormat switch
        {
            PbtGroupFormat.Interleaved or PbtGroupFormat.Every3Depth => PbtLeafFormat.Interleaved,
            PbtGroupFormat.BoundaryOnly => PbtLeafFormat.LeavesOnly,
            PbtGroupFormat.Every4Depth => PbtLeafFormat.Every4Depth,
            _ => PbtLeafFormat.EveryLevel,
        };

        /// <summary>Folds one stem's writes (<paramref name="changes"/>) into its leaf blob, persists it, and reports whether the stem is now empty.</summary>
        /// <param name="knownAbsent">
        /// The stem had no node in the trie, so — a stem node and its leaf blob being born and dying
        /// together, keyed by the stem — there is no stored blob to merge and the read is skipped.
        /// </param>
        private bool ComputeBlob(
            in Stem stem, IPbtStemChanges changes, bool knownAbsent, in ValueHash256 priorSubtreeRoot,
            out ValueHash256 subtreeRoot)
        {
            using RefCountingMemory? prior = knownAbsent ? null : _store.GetLeafBlob(stem, priorSubtreeRoot);
            using StemLeafBlob.RebuildState newBlob = StemLeafBlob.Apply(prior is null ? default : prior.GetSpan(), changes, _memoryProvider, LeafFormat);
            subtreeRoot = newBlob.SubtreeRoot;
            bool isEmpty = newBlob.IsEmpty;
            _store.SetLeafBlob(stem, subtreeRoot, newBlob.Take());
            return isEmpty;
        }

        /// <summary>
        /// Checks the shape off its touched slots against the occupants, which it must say exactly what
        /// the frame started holding at.
        /// </summary>
        /// <remarks>
        /// The descent settles only what it visits, so a caller naming the wrong starting shape leaves the
        /// rebuild blind to its untouched children — a wrong fold, and a wrong state root. It shows up at
        /// once for a stored group, whose untouched slots are usually many; a seeded frame has one slot
        /// that is often touched anyway, so there it could pass unnoticed.
        /// </remarks>
        [Conditional("DEBUG")]
        private static void AssertUntouchedMerged(
            in TreeReader<TLayout> occupants, in PbtTrieNodeGroup<TLayout> existing,
            in BoundarySlotMasks<TLayout> boundary, SlotBitmask<TLayout> touched)
        {
            for (int slot = 0; slot < TLayout.BoundarySlots; slot++)
            {
                if (touched[slot]) continue;

                NodeKind kind = occupants.Reader(slot, existing).Occupant.Kind;

                // a constant message: Debug.Assert evaluates its argument on every call, and this one runs
                // per untouched slot of every frame
                Debug.Assert(
                    boundary.Presence[slot] == (kind != NodeKind.Absent)
                    && boundary.Stems[slot] == (kind == NodeKind.Stem)
                    && boundary.Chains[slot] == (kind == NodeKind.Chain),
                    "an untouched slot disagrees with its occupant — the caller named the wrong starting shape");
            }
        }

        /// <summary>
        /// Checks that compact counts and their touched mask describe the whole frame exactly once.
        /// </summary>
        /// <remarks>
        /// The mask crosses a format boundary between optional precalculated levels and runtime partitioning,
        /// so the count order, positivity, total and zero tail are all validated together.
        /// </remarks>
        [Conditional("DEBUG")]
        private static void AssertCompactLevel(scoped PartitionOutcome outcome, int entryCount)
        {
            PbtWriteBatch.BucketLevel<TLayout> buckets = outcome.IterateBuckets();
            int total = 0;
            foreach ((_, Range range) in buckets)
            {
                int count = range.End.Value - range.Start.Value;
                Debug.Assert(count > 0, "a touched slot must have a positive compact count");
                total += count;
            }

            Debug.Assert(total == entryCount, "compact counts must cover the frame's entries");
            ReadOnlySpan<int> counts = outcome.Level[..^PbtWriteBatch.TouchedWordCount<TLayout>()];
            ReadOnlySpan<int> unusedCounts = counts[buckets.Touched.Count..];
            foreach (int count in unusedCounts)
            {
                Debug.Assert(count == 0, "the unused compact-count tail must be zero");
            }
        }

        /// <summary>
        /// What the descent already knows about a range before a frame partitions it: the bucket levels
        /// the producer precalculated for it, and the depth of the group where its stems first part.
        /// </summary>
        /// <remarks>
        /// The branch depth is what spares the shared-prefix corridors the key derivation builds in — every
        /// storage stem of one contract shares 61 bits, so a batch of one contract's slots falls in a single
        /// bucket at each of the fifteen groups above the depth-60 one. Found once by
        /// <see cref="Partition"/>, it rides down the descent so those frames fill their level from the
        /// entry count alone rather than walking the range again per level. Unlike a level it survives a
        /// depth jump: it is a property of the stem set, not of one range at one depth.
        /// </remarks>
        /// <param name="precalculated">
        /// The bucket table levels the producer already partitioned the range into, the frame's own being
        /// the last of them (<see cref="PbtWriteBatch.BucketTableLength"/>); empty once the descent runs
        /// past them, from where each group buckets its range itself.
        /// </param>
        /// <param name="branchDepth">
        /// The depth of the group where the range's stems first part, or <c>0</c> when nothing is known —
        /// which every <c>default</c> plan reports, no frame sitting above depth 0.
        /// </param>
        private readonly ref struct PartitionResult
        {
            private readonly BucketPlan _plan;

            public PartitionResult(BucketPlan plan, int branchDepth, bool isSorted)
            {
                _plan = plan;
                BranchDepth = branchDepth;
                IsSorted = isSorted;
            }

            public int BranchDepth { get; }

            public bool IsSorted { get; }

            public BucketPlan GetChildPlan(int bucketIdx) => _plan.ForChild(bucketIdx, BranchDepth, IsSorted);
        }

        private readonly ref struct BucketPlan(ReadOnlySpan<int> precalculated, int branchDepth, bool isSorted)
        {
            /// <inheritdoc cref="BucketPlan" path="/param[@name='precalculated']"/>
            public ReadOnlySpan<int> Precalculated { get; } = precalculated;

            /// <inheritdoc cref="BucketPlan" path="/param[@name='branchDepth']"/>
            public int BranchDepth { get; } = branchDepth;

            /// <summary>Whether the complete stems are in ascending lexicographic order.</summary>
            public bool IsSorted { get; } = isSorted;

            public int GetBufferSize(int entryCount, int depth) => HasPrecalculatedLevel
                ? 0
                : sizeof(int) * ((BranchDepth > depth ? 1 : Math.Min(entryCount, TLayout.BoundarySlots)) + PbtWriteBatch.TouchedWordCount<TLayout>());

            public PartitionOutcome BucketSort(
                Span<PbtWriteBatch.StemEntry> entries, int depth, Span<byte> buffer)
            {
                if (HasPrecalculatedLevel)
                {
                    return new PartitionOutcome(
                        Precalculated[^PbtWriteBatch.LevelStride<TLayout>()..], BranchDepth, IsSorted);
                }

                Span<int> computed = MemoryMarshal.Cast<byte, int>(buffer);
                if (BranchDepth > depth)
                {
                    FillSingleBucket(TLayout.SlotOf(entries[0].Stem, depth), entries.Length, computed);
                    return new PartitionOutcome(computed, BranchDepth, IsSorted);
                }

                return IsSorted
                    ? new PartitionOutcome(computed, PopulateSortedLevel(entries, depth, computed), true)
                    : Partition(entries, depth, buffer);
            }

            private bool HasPrecalculatedLevel => Precalculated.Length >= PbtWriteBatch.LevelStride<TLayout>();

            /// <summary>
            /// The plan for the child group under boundary slot <paramref name="slot"/>, whose range this
            /// frame's bucketing found to part at <paramref name="branchDepth"/>.
            /// </summary>
            /// <remarks>
            /// The precalculated levels are laid out coarsest-last, each one
            /// <see cref="PbtWriteBatch.LevelStride<TLayout>()"/> wide per group it describes, so the finer levels of a
            /// slot's subtree are the slice at its own index — and what remains after this group's own level
            /// divides evenly among the sixteen slots.
            /// </remarks>
            public BucketPlan ForChild(int slot, int branchDepth, bool isSorted)
            {
                ReadOnlySpan<int> buckets = Precalculated;
                int childLength = buckets.Length <= PbtWriteBatch.LevelStride<TLayout>()
                    ? 0
                    : (buckets.Length - PbtWriteBatch.LevelStride<TLayout>()) / TLayout.BoundarySlots;
                return new BucketPlan(childLength == 0 ? default : buckets.Slice(slot * childLength, childLength), branchDepth, isSorted);
            }

            /// <summary>The plan a frame deeper down the same range inherits, the levels skipped over describing neither depth.</summary>
            public BucketPlan AfterJump() => new(default, BranchDepth, IsSorted);
        }

        /// <summary>
        /// Fills <paramref name="level"/> for a range that falls wholly in bucket <paramref name="slot"/>,
        /// in the shape <see cref="Partition"/> would have produced for it.
        /// </summary>
        private static void FillSingleBucket(int slot, int count, Span<int> level)
        {
            Span<int> counts = level[..^PbtWriteBatch.TouchedWordCount<TLayout>()];
            counts.Clear();
            counts[0] = count;
            PbtWriteBatch.ClearTouched<TLayout>(level);
            PbtWriteBatch.SetTouched<TLayout>(level, slot);
        }

        /// <summary>
        /// Checks that <paramref name="claimed"/> — the branch depth a frame inherited for
        /// <paramref name="entries"/> — does not reach past where they really part.
        /// </summary>
        /// <remarks>
        /// The one error here that would reach the store rather than fail: a range claimed to part deeper
        /// than it does buckets into one slot that its siblings' entries then descend, minting a run across
        /// levels the trie actually branches at, and nothing but the root would differ. Under-claiming is
        /// legal and routine — a child of a frame that branched inherits that frame's own depth — so this
        /// is a bound, not an equality.
        /// </remarks>
        [Conditional("DEBUG")]
        private void AssertBranchDepthSound(ReadOnlySpan<PbtWriteBatch.StemEntry> entries, int depth, int claimed)
        {
            if (claimed <= depth) return;

            Stem reference = entries[0].Stem;
            int splitDepth = Stem.LengthInBits;
            for (int i = 0; i < entries.Length; i++)
            {
                int diff = entries[i].Stem.FirstDifferingBit(reference, depth);
                if ((uint)diff < (uint)splitDepth) splitDepth = diff;
            }

            Debug.Assert(
                claimed <= TLayout.GroupDepthOf(splitDepth),
                "the inherited branch depth reaches past where the range parts");
        }

        /// <summary>
        /// Radix-partitions <paramref name="entries"/> (sharing bits <c>[0, depth)</c>, any order) in place
        /// into the boundary buckets of the group at <paramref name="depth"/>. Small ranges are sorted by
        /// their complete stems; larger ones use an in-place American-flag partition whose within-bucket
        /// order is arbitrary.
        /// </summary>
        /// <param name="buffer">
        /// Scratch storage sized by <see cref="BucketPlan.GetBufferSize(int, int)"/> for compact counts and the
        /// touched-slot mask.
        /// </param>
        /// <returns>
        /// The depth of the group where <paramref name="entries"/> first part from one another: this
        /// group's own whenever two of them fall in different buckets, and a deeper one when they all
        /// share its slot — the levels between hold a single child apiece, so a caller free to skip
        /// them can jump straight to the group that branches.
        /// </returns>
        private readonly ref struct PartitionOutcome(
            ReadOnlySpan<int> level, int branchDepth, bool isSorted)
        {
            public ReadOnlySpan<int> Level { get; } = level;

            public int BranchDepth { get; } = branchDepth;

            public bool IsSorted { get; } = isSorted;

            public PbtWriteBatch.BucketLevel<TLayout> IterateBuckets() => PbtWriteBatch.ReadLevel<TLayout>(Level);
        }

        private static PartitionOutcome Partition(Span<PbtWriteBatch.StemEntry> entries, int depth, Span<byte> buffer)
        {
            Span<int> level = MemoryMarshal.Cast<byte, int>(buffer);
            if (entries.Length <= TinyRange) return SortTiny(entries, depth, level);
            if (entries.Length <= FullSortThreshold)
            {
                SortByStem(entries);
                return new PartitionOutcome(level, PopulateSortedLevel(entries, depth, level), true);
            }

            Span<int> slotCounts = stackalloc int[TLayout.BoundarySlots];
            slotCounts.Clear();
            Stem reference = entries[0].Stem;
            int floor = depth + TLayout.LevelsPerGroup;
            int splitDepth = Stem.LengthInBits;
            for (int i = 0; i < entries.Length; i++)
            {
                Stem stem = entries[i].Stem;
                slotCounts[TLayout.SlotOf(stem, depth)]++;
                if (splitDepth > floor)
                {
                    int diff = stem.FirstDifferingBit(reference, depth);
                    if ((uint)diff < (uint)splitDepth) splitDepth = diff;
                }
            }

            Span<int> starts = stackalloc int[TLayout.BoundarySlots + 1];
            Span<int> compactCounts = level[..^PbtWriteBatch.TouchedWordCount<TLayout>()];
            compactCounts.Clear();
            PbtWriteBatch.ClearTouched<TLayout>(level);
            int total = 0;
            int countIndex = 0;
            for (int slot = 0; slot < TLayout.BoundarySlots; slot++)
            {
                starts[slot] = total;
                int count = slotCounts[slot];
                if (count != 0)
                {
                    compactCounts[countIndex++] = count;
                    PbtWriteBatch.SetTouched<TLayout>(level, slot);
                }
                total += count;
            }
            starts[TLayout.BoundarySlots] = total;
            Debug.Assert(total == entries.Length);

            if (countIndex == 1)
            {
                Debug.Assert(splitDepth >= floor, "one populated bucket means nothing parts within this group");
                return new PartitionOutcome(level, TLayout.GroupDepthOf(splitDepth), false);
            }

            Span<int> heads = stackalloc int[TLayout.BoundarySlots];
            starts[..TLayout.BoundarySlots].CopyTo(heads);
            for (int slot = 0; slot < TLayout.BoundarySlots; slot++)
            {
                while (heads[slot] < starts[slot + 1])
                {
                    int target = TLayout.SlotOf(entries[heads[slot]].Stem, depth);
                    if (target == slot)
                    {
                        heads[slot]++;
                    }
                    else
                    {
                        (entries[heads[slot]], entries[heads[target]]) = (entries[heads[target]], entries[heads[slot]]);
                        heads[target]++;
                    }
                }
            }

            return new PartitionOutcome(level, depth, false);
        }

        private static int PopulateSortedLevel(ReadOnlySpan<PbtWriteBatch.StemEntry> entries, int depth, Span<int> level)
        {
            Span<int> counts = level[..^PbtWriteBatch.TouchedWordCount<TLayout>()];
            counts.Clear();
            PbtWriteBatch.ClearTouched<TLayout>(level);
            Stem first = entries[0].Stem;
            int diff = first.FirstDifferingBit(entries[^1].Stem, depth);
            int splitDepth = (uint)diff < Stem.LengthInBits ? diff : Stem.LengthInBits;
            int countIndex = 0;
            int runSlot = TLayout.SlotOf(first, depth);
            PbtWriteBatch.SetTouched<TLayout>(level, runSlot);
            for (int i = 0; i < entries.Length; i++)
            {
                Stem stem = entries[i].Stem;
                int slot = TLayout.SlotOf(stem, depth);
                Debug.Assert(slot >= runSlot, "complete stem order must yield ascending slots at every depth");
                if (slot != runSlot)
                {
                    countIndex++;
                    runSlot = slot;
                    PbtWriteBatch.SetTouched<TLayout>(level, slot);
                }
                counts[countIndex]++;
            }

            return TLayout.GroupDepthOf(splitDepth);
        }

        private static PartitionOutcome SortTiny(Span<PbtWriteBatch.StemEntry> entries, int depth, Span<int> level)
        {
            Debug.Assert(!entries.IsEmpty && entries.Length <= TinyRange);
            if (entries.Length > 1)
            {
                CompareAndSwap(entries, 0, 1);
                if (entries.Length > 2)
                {
                    CompareAndSwap(entries, 1, 2);
                    CompareAndSwap(entries, 0, 1);
                }
            }

            return new PartitionOutcome(level, PopulateSortedLevel(entries, depth, level), true);
        }

        private static void SortByStem(Span<PbtWriteBatch.StemEntry> entries) => entries.Sort(StemEntryComparer.Instance);

        private static void CompareAndSwap(Span<PbtWriteBatch.StemEntry> entries, int i, int j)
        {
            if (StemEntryComparer.Instance.Compare(entries[i], entries[j]) <= 0) return;
            (entries[i], entries[j]) = (entries[j], entries[i]);
        }

        private sealed class StemEntryComparer : IComparer<PbtWriteBatch.StemEntry>
        {
            public static readonly StemEntryComparer Instance = new();

            public int Compare(PbtWriteBatch.StemEntry x, PbtWriteBatch.StemEntry y)
            {
                Stem xStem = x.Stem;
                Stem yStem = y.Stem;
                return xStem.Bytes.SequenceCompareTo(yStem.Bytes);
            }
        }
    }
}
