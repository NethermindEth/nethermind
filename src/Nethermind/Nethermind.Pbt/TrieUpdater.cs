// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;

namespace Nethermind.Pbt;

/// <summary>
/// Backing store for the PBT tree: the stem trie node groups and the per-stem 256-leaf blobs. Reads
/// return a poolable, disposable wrapper (null = absent); writes hand over one (null removes the group
/// / deletes the stem).
/// </summary>
/// <remarks>
/// A write transfers the caller's lease on the value: the store owns it from that point and must
/// release it once done — by disposing it after copying, or, if it retains the memory, when it drops
/// the value. It never acquires a lease of its own to do so, and the caller must not use the memory
/// afterwards; a caller that needs to keep reading it acquires its own lease first.
/// <para>
/// An implementation must bear reads and writes from several threads at once: a fold runs across as
/// many threads as it is given, each reading and writing as it goes. What it needs of them is
/// structural safety and nothing more — two threads never touch the same node key or stem, since the
/// ranges they fold are disjoint subtrees, and a value is always read before it is written.
/// </para>
/// </remarks>
public interface IPbtStore
{
    RefCountingMemory? GetTrieNode(in TrieNodeKey key);
    void SetTrieNode(in TrieNodeKey key, RefCountingMemory? node);
    RefCountingMemory? GetLeafBlob(in Stem stem);
    void SetLeafBlob(in Stem stem, RefCountingMemory? blob);
}

/// <summary>
/// Applies a batch of EIP-8297 tree-key writes and returns the new root. It folds each affected
/// stem's leaf blob (<see cref="StemLeafBlob"/>), then maintains the top-level binary trie of stems
/// stored as 4-level <see cref="PbtTrieNodeGroup"/> tiles, with single-child runs between them
/// collapsed into <see cref="PbtNodeChain"/> blobs.
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
    /// <param name="writeFormat">
    /// Which encoding to write the groups this batch rebuilds in, and with them the leaf blobs (see
    /// <see cref="PbtLeafFormat"/>). They all fold to the same root and are all read whatever this says,
    /// so it may change between batches over one store: a group is converted only by a change that
    /// rewrites it anyway.
    /// </param>
    /// <param name="delta">The change this batch makes to the whole tree's stem count — positive as stems are added, negative as their leaves are zeroed away.</param>
    /// <param name="concurrency">
    /// How many threads may fold the batch: <c>0</c> for the processor count, <c>1</c> to fold on the
    /// calling thread alone. A batch too small to be worth splitting folds on the calling thread
    /// whatever this says.
    /// </param>
    public static ValueHash256 UpdateRoot(
        IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch changes, IRefCountingMemoryProvider memoryProvider,
        PbtGroupFormat writeFormat, int concurrency, out PbtSubtreeStats delta)
    {
        if (changes.Count == 0)
        {
            delta = default;
            return currentRoot;
        }

        return new Updater(store, memoryProvider, writeFormat, changes, concurrency).Run(currentRoot, changes, out delta);
    }

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
    private sealed partial class Updater
    {
        /// <summary>The largest range <see cref="SortTiny"/> takes off <see cref="Partition"/>'s hands.</summary>
        private const int TinyRange = 3;

        private readonly IPbtStore _store;
        private readonly IRefCountingMemoryProvider _memoryProvider;
        private readonly PbtGroupFormat _writeFormat;

        /// <summary>The batch's entries, which the frames permute in place — each job over its own range of them.</summary>
        private readonly PbtWriteBatch.StemEntry[] _entries;

        /// <inheritdoc cref="PbtWriteBatch.Buckets"/>
        private readonly int[]? _buckets;

        /// <summary>The smallest bucket worth handing to another thread; <see cref="int.MaxValue"/> on a fold that stays on the calling thread.</summary>
        private readonly int _minQueueEntries;

        /// <summary>
        /// A stored blob as the descent reads it: its bytes, and where they sit in the memory holding
        /// them. A blob the store handed over is all of its memory; one the frame above holds in a
        /// <see cref="PbtNodeCluster"/> is a slice of that frame's, and the entry offsets an
        /// <see cref="Occupant"/> keeps are into the memory rather than the blob, which is why the two
        /// travel together.
        /// </summary>
        private readonly struct StoredBlob(RefCountingMemory? memory, int offset, int length)
        {
            /// <summary>The whole of <paramref name="memory"/>, as the store hands a blob over; the absent blob for <c>null</c>.</summary>
            public static StoredBlob Of(RefCountingMemory? memory) =>
                memory is null ? default : new(memory, 0, memory.GetSpan().Length);

            public bool IsEmpty => memory is null;

            /// <inheritdoc cref="StoredBlob" path="/summary"/>
            public RefCountingMemory? Memory => memory;

            /// <summary>Where these bytes start in <see cref="Memory"/>.</summary>
            public int Offset => offset;

            public ReadOnlySpan<byte> Data => memory is null ? default : memory.GetSpan().Slice(offset, length);

            /// <summary>The blob <paramref name="range"/> of these bytes holds; the absent blob for an empty range.</summary>
            public StoredBlob Slice(Range range)
            {
                (int start, int sliceLength) = range.GetOffsetAndLength(length);
                return sliceLength == 0 ? default : new StoredBlob(memory, offset + start, sliceLength);
            }
        }

        private ValueHash256 Descend(
            in ValueHash256 currentRoot, PbtWriteBatch changes, in Fanout fanout, out PbtSubtreeStats delta)
        {
            using RefCountingMemory? rootData = _store.GetTrieNode(TrieNodeKey.Root);
            BufferWriter writer = new(_memoryProvider);
            ApplyGroup(
                TrieNodeKey.Root, changes.Entries, StoredBlob.Of(rootData), currentRoot,
                new BucketPlan(changes.Buckets, branchDepth: 0), fanout, ref writer, out NodeResult root, out bool changed, out delta);
            if (writer.Detach() is { } folded) root = root.WithBlob(folded);

            using (root)
            {
                // The root has no parent to persist it, so the top frame does: plant its rebuilt blob, or
                // remove the key when the tree has emptied out.
                if (changed && root.Blob is { } rootBlob)
                {
                    rootBlob.AcquireLease();
                    _store.SetTrieNode(TrieNodeKey.Root, rootBlob);
                }
                else if (changed && root.Kind == NodeKind.Absent && rootData is not null)
                {
                    _store.SetTrieNode(TrieNodeKey.Root, null);
                }

                return root.NodeHash();
            }
        }

        /// <summary>
        /// The boundary occupants a group frame descends from, read on demand by slot. The lease stays with
        /// the source — the stored encoding or the seed — so a read transient is never disposed.
        /// </summary>
        private interface IOccupants
        {
            /// <summary>
            /// Whether the occupants sit on a stored group encoding, which an untouched slot's value can be
            /// read back out of later; a seeded frame has none, so its occupants must ride on in the results.
            /// </summary>
            bool HasStoredEncoding { get; }

            /// <summary>The occupant at boundary <paramref name="slot"/>, or <c>default</c> when the slot holds none.</summary>
            Occupant this[int slot] { get; }

            /// <summary>
            /// The blob of the child group under boundary <paramref name="slot"/> where this frame holds
            /// it rather than the store, and the absent blob where the store does.
            /// </summary>
            /// <remarks>
            /// A frame at a <see cref="PbtLayout.IsClusteringDepth"/> depth holds every one of them:
            /// its children have no keys of their own, so this is the only way to their bytes, whether the
            /// descent is about to read one or is copying an untouched one straight over into the blob
            /// replacing this one.
            /// </remarks>
            StoredBlob ChildBlob(int slot);

            /// <summary>
            /// The slots <see cref="ChildBlob"/> answers for, which a clustering frame walks alongside the
            /// slots its writes touch: a child no write reaches still has to be carried over.
            /// </summary>
            uint ChildSlotsBitmask { get; }
        }

        /// <summary>
        /// The occupants of a stored group, each read straight out of its encoding at the slot's fixed
        /// boundary position, and the children the blob holds around that encoding.
        /// </summary>
        private readonly ref struct StoredOccupants(PbtTrieNodeGroup existing, StoredBlob blob, PbtNodeCluster cluster) : IOccupants
        {
            private readonly PbtTrieNodeGroup _existing = existing;
            private readonly StoredBlob _blob = blob;
            private readonly PbtNodeCluster _cluster = cluster;

            public bool HasStoredEncoding => !_existing.IsEmpty;

            public Occupant this[int slot]
            {
                get
                {
                    int position = PbtLayout.TrieNodeGroupBoundarySlotPosition(slot);
                    NodeKind kind = _existing.KindAt(position);
                    return kind == NodeKind.Absent
                        ? default
                        : new Occupant(new NodeRef(kind, GroupOffset + _existing.EntryOffset(position)), _blob.Memory);
                }
            }

            public StoredBlob ChildBlob(int slot) => _blob.Slice(_cluster.Child(slot, _existing));

            public uint ChildSlotsBitmask => _cluster.IsBare ? 0 : _existing.BoundaryShape().ChildSlots;

            /// <summary>Where the group's entries start in the memory holding them, which a cluster puts its children ahead of.</summary>
            private int GroupOffset => _blob.Offset + _cluster.GroupOffset;
        }

        /// <summary>The lone occupant a run-split or pushed stem seeds into an otherwise empty frame, held at its slot.</summary>
        /// <param name="seedBlob">
        /// The blob under the seed, for the one seed that has one: a run split adopting the target group
        /// its new frame clusters. Absent otherwise — a pushed stem roots no blob, and a seeded run carries
        /// its own encoding in the occupant.
        /// </param>
        private readonly struct SeededOccupant(Occupant seed, int seedSlot, StoredBlob seedBlob) : IOccupants
        {
            private readonly Occupant _seed = seed;
            private readonly int _seedSlot = seedSlot;
            private readonly StoredBlob _seedBlob = seedBlob;

            public bool HasStoredEncoding => false;

            public Occupant this[int slot] => slot == _seedSlot ? _seed : default;

            public StoredBlob ChildBlob(int slot) => slot == _seedSlot ? _seedBlob : default;

            public uint ChildSlotsBitmask => _seedBlob.IsEmpty ? 0 : 1u << _seedSlot;
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
        /// call: the occupants read out of it take their own. Where the group's depth holds its children,
        /// the blob holds them too, and they are read — and carried over — straight out of it.
        /// </param>
        /// <param name="beforeHash"><inheritdoc cref="RebuildNode" path="/param[@name='beforeHash']"/></param>
        /// <param name="plan"><inheritdoc cref="ResolveBoundaries" path="/param[@name='plan']"/></param>
        /// <param name="fanout">
        /// Where this frame hands the buckets it is not folding itself. Carried down the descent rather
        /// than held, since the queue belongs to whichever thread is running the fold, not to the updater.
        /// </param>
        /// <param name="writer">Where the group folds its encoding: the blob of the group above where that one holds this, a buffer of this group's own where it owns its blob.</param>
        /// <param name="result">The node now occupying the group's root position, written straight into the caller's slot.</param>
        /// <param name="changed"><inheritdoc cref="RebuildNode" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="RebuildNode" path="/param[@name='delta']"/></param>
        private void ApplyGroup(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, StoredBlob existingData,
            in ValueHash256 beforeHash, scoped BucketPlan plan, in Fanout fanout, ref BufferWriter writer,
            out NodeResult result, out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty && depth % PbtLayout.TrieNodeGroupLevelsPerGroup == 0);
            Debug.Assert(depth <= PbtLayout.TrieNodeGroupMaxGroupDepth);
            Debug.Assert(!PbtLayout.IsClusteringDepth(depth), "a group holding its children goes through ApplyClustered");

            PbtTrieNodeGroup existing = existingData.IsEmpty ? default : PbtTrieNodeGroup.Decode(existingData.Data);

            RefList16<NodeResult> resultBuffer = new(PbtLayout.TrieNodeGroupBoundarySlots);
            Span<NodeResult> results = resultBuffer.AsSpan();
            int mark = writer.WrittenCount;
            PbtNodeCluster.Builder bare = default;

            StoredOccupants occupants = new(existing, existingData, default);
            GroupShape shape = ResolveBoundaries(key, entries, occupants, plan, fanout, results, ref writer, ref bare)
                .MergeUntouched(existing.BoundaryShape());
            result = RebuildNode(
                key, occupants, existing, results, shape, beforeHash, existing.Stats, ref writer, ref bare, mark,
                out changed, out delta);
        }

        private void ApplyClustered(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, StoredBlob existingData,
            in ValueHash256 beforeHash, scoped BucketPlan plan, in Fanout fanout, ref BufferWriter writer,
            out NodeResult result, out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty && depth % PbtLayout.TrieNodeGroupLevelsPerGroup == 0);
            Debug.Assert(depth <= PbtLayout.TrieNodeGroupMaxGroupDepth);
            Debug.Assert(PbtLayout.IsClusteringDepth(depth), "a group holding no child goes through ApplyGroup");

            PbtTrieNodeGroup existing = default;
            PbtNodeCluster cluster = existingData.IsEmpty ? default : PbtNodeCluster.Decode(existingData.Data, out existing);

            PbtNodeCluster.Builder builder = default;
            RefList16<NodeResult> resultBuffer = new(PbtLayout.TrieNodeGroupBoundarySlots);
            Span<NodeResult> results = resultBuffer.AsSpan();

            StoredOccupants occupants = new(existing, existingData, cluster);
            GroupShape shape = ResolveBoundaries(key, entries, occupants, plan, fanout, results, ref writer, ref builder)
                .MergeUntouched(existing.BoundaryShape());
            result = RebuildNode(
                key, occupants, existing, results, shape, beforeHash, existing.Stats, ref writer, ref builder, mark: 0,
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
        private void ApplyPushedStem(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in Occupant pushed,
            scoped BucketPlan plan, in Fanout fanout, ref BufferWriter writer, out NodeResult result,
            out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty && depth % PbtLayout.TrieNodeGroupLevelsPerGroup == 0);
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
                bool isEmpty = ComputeBlob(stem, entries[0].Changes, pushed.Kind == NodeKind.Absent, out ValueHash256 subtreeRoot);
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
            if (depth == Stem.LengthInBits) throw new InvalidOperationException($"Stem {stem} written more than once in a single batch");

            Debug.Assert(depth <= PbtLayout.TrieNodeGroupMaxGroupDepth);

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
            Span<int> computed = stackalloc int[PbtWriteBatch.LevelStride];
            int entriesBranch = plan.BranchDepth;
            int branchDepth = depth;
            if (buckets.IsEmpty)
            {
                if (entriesBranch <= depth)
                {
                    entriesBranch = Partition(entries, depth, computed);
                    buckets = computed;
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
                int pushedBranch = (uint)diff < Stem.LengthInBits ? diff & ~(PbtLayout.TrieNodeGroupLevelsPerGroup - 1) : Stem.LengthInBits;
                if (pushedBranch < branchDepth) branchDepth = pushedBranch;
            }

            // Stems that never part leave the branch past the trie's last group, which no key names: that
            // is the duplicate-stem batch, left to the descent to reject where it already does.
            if (branchDepth > depth && branchDepth <= PbtLayout.TrieNodeGroupMaxGroupDepth)
            {
                Debug.Assert(depth > 0, "a run starts past the root (invariant 4)");
                TrieNodeKey branchKey = TrieNodeKey.For(branchDepth, stem);

                // The same frame again at the group the run ends in, which is the ordinary descent from
                // there down: the jump cannot fire a second time, whatever bounded it — the entries or the
                // pushed stem — parting at that very depth. The pushed occupant rides along to be placed
                // there rather than here, and the entries' own branch depth with it, which still stands
                // where the pushed stem is what cut the run short. A run is about to be minted above that
                // group, so it is one no blob of this frame's can hold: it owns its own.
                BufferWriter branchWriter = new(_memoryProvider);
                ApplyPushedStem(
                    branchKey, entries, pushed, new BucketPlan(default, entriesBranch), fanout, ref branchWriter,
                    out NodeResult inner, out _, out delta);
                if (branchWriter.Detach() is { } innerBlob) inner = inner.WithBlob(innerBlob);

                // A run's node hash is its path's, unlike a stem's, so the inner frame's answer to whether
                // it changed is about a node starting at the wrong depth. It is settled here instead, once
                // the run reaching down from this frame is what the pushed occupant is compared against.
                result = WrapIntoChain(depth, branchKey, inner, innerBlobStored: false, beforeStats + delta);
                changed = result.NodeHash() != pushed.NodeHash();
                return;
            }

            uint occupantsOccupied = 0;
            int seedSlot = -1;
            if (pushed.Kind == NodeKind.Stem)
            {
                seedSlot = NibbleOf(pushed.Stem, depth);
                occupantsOccupied = 1u << seedSlot;
            }

            // a pushed stem roots no blob of its own: its subtree is its leaf blob, keyed by the stem
            SeededOccupant occupants = new(pushed, seedSlot, default);
            RefList16<NodeResult> resultBuffer = new(PbtLayout.TrieNodeGroupBoundarySlots);
            Span<NodeResult> results = resultBuffer.AsSpan();

            PbtNodeCluster.Builder builder = default;
            int mark = writer.WrittenCount;

            // `buckets` carries the level partitioned above where there was none to start with, and
            // `entriesBranch` lets the resolve fill one itself where nothing was partitioned at all, so the
            // range is bucketed once however this frame reached here.
            GroupShape shape = ResolveBoundaries(key, entries, occupants, new BucketPlan(buckets, entriesBranch), fanout, results, ref writer, ref builder)
                .MergeUntouched(new NodeGroupBitmasks(occupantsOccupied, occupantsOccupied, Chains: 0));
            // A frame with no stored encoding leaves the rebuild nothing but `results` to read a boundary
            // out of, so the pushed stem rides on in its slot unless the descent already refreshed it. It
            // promotes to a pure value, so it carries no lease.
            if (seedSlot >= 0 && (shape.TouchedBitmask >> seedSlot & 1) == 0) results[seedSlot] = pushed;

            result = RebuildNode(
                key, occupants, default, results, shape, pushed.NodeHash(), beforeStats, ref writer, ref builder, mark,
                out changed, out delta);
        }

        /// <summary>
        /// The group's boundary shape once its touched slots are resolved: the masks and the stat delta the
        /// rebuild reads.
        /// </summary>
        /// <remarks>
        /// Built in two phases. <see cref="ResolveBoundaries"/> knows only the slots it descended, so the
        /// shape it returns describes those alone; the caller completes it with
        /// <see cref="MergeUntouched"/> before handing it to <see cref="RebuildNode"/>, which needs every
        /// slot.
        /// </remarks>
        private readonly record struct GroupShape(
            NodeGroupBitmasks Boundary, uint ChangedBitmask, uint TouchedBitmask, uint StoredChildBitmask, PbtSubtreeStats Deltas)
        {
            /// <summary>
            /// Folds the slots the descent never visited back in, from the shape
            /// <paramref name="untouched"/> the frame started with.
            /// </summary>
            /// <remarks>
            /// The descent's own bits are a subset of <see cref="TouchedBitmask"/>, so the argument contributes
            /// only outside it and the two halves never collide.
            /// </remarks>
            public GroupShape MergeUntouched(NodeGroupBitmasks untouchedBitmask) => this with
            {
                Boundary = new NodeGroupBitmasks(
                    Boundary.Presence | (untouchedBitmask.Presence & ~TouchedBitmask),
                    Boundary.Stems | (untouchedBitmask.Stems & ~TouchedBitmask),
                    Boundary.Chains | (untouchedBitmask.Chains & ~TouchedBitmask)),
            };
        }

        /// <summary>
        /// Descends only the non-empty buckets of the group at <paramref name="key"/>, applying each touched
        /// slot's writes to its child and settling the result into <paramref name="results"/>. An untouched
        /// slot is left alone entirely — it keeps its occupant, which the settle reads on demand.
        /// </summary>
        /// <returns>
        /// The shape of the slots this descended, and of those alone: the frame's untouched slots are none
        /// of its business, so the caller folds them back in with <see cref="GroupShape.MergeUntouched"/>
        /// before <see cref="RebuildNode"/>, which needs the whole picture.
        /// </returns>
        /// <param name="occupants">
        /// The frame's boundary occupants, read on demand by slot as the descent needs them: a
        /// <see cref="StoredOccupants"/> reading a stored group's encoding, or a <see cref="SeededOccupant"/>
        /// holding the one node a run-split or pushed stem seeds.
        /// </param>
        /// <param name="plan"><inheritdoc cref="BucketPlan" path="/summary"/></param>
        /// <param name="fanout"><inheritdoc cref="ApplyGroup" path="/param[@name='fanout']"/></param>
        /// <param name="writer"><inheritdoc cref="ApplyGroup" path="/param[@name='writer']"/></param>
        /// <param name="cluster">The blob this frame is assembling, where its depth holds its children inside it.</param>
        private GroupShape ResolveBoundaries<TOccupants>(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in TOccupants occupants,
            scoped BucketPlan plan, in Fanout fanout, Span<NodeResult> results, ref BufferWriter writer,
            ref PbtNodeCluster.Builder cluster)
            where TOccupants : IOccupants, allows ref struct
        {
            int depth = key.Depth;
            AssertBranchDepthSound(entries, depth, plan.BranchDepth);

            // A precalculated level is already a bounds array over this very range, so it is read where it
            // lies; a range known not to part until deeper than this group falls in one bucket, so its
            // level follows from the count alone; only what neither covers is partitioned here.
            Span<int> computed = stackalloc int[PbtWriteBatch.LevelStride];
            scoped ReadOnlySpan<int> level;
            int branchDepth = plan.BranchDepth;
            if (!plan.Precalculated.IsEmpty)
            {
                level = plan.Precalculated[^PbtWriteBatch.LevelStride..];
            }
            else if (branchDepth > depth)
            {
                FillSingleBucket(NibbleOf(entries[0].Stem, depth), entries.Length, computed);
                level = computed;
            }
            else
            {
                branchDepth = Partition(entries, depth, computed);
                level = computed;
            }

            uint touchedBitmask = (uint)level[PbtWriteBatch.TouchedMaskIndex];
            AssertTouchedMaskMatchesBounds(touchedBitmask, level[..PbtWriteBatch.BoundsLength]);

            return PbtLayout.IsClusteringDepth(depth)
                ? ResolveClusteredChildren(key, entries, occupants, plan, fanout, results, level, branchDepth, ref writer, ref cluster)
                : ResolveKeyedChildren(key, entries, occupants, plan, fanout, results, level, branchDepth);
        }

        /// <summary>
        /// <inheritdoc cref="ResolveBoundaries" path="/summary"/> For a frame whose blob holds its
        /// children, so the walk covers every slot rooting one on top of those a write touches: an
        /// untouched child is carried over from the blob being replaced, its bytes living nowhere else.
        /// Both masks are ascending and walked as one, which is what keeps the children going into the
        /// blob in the order they are read back out of it.
        /// </summary>
        private GroupShape ResolveClusteredChildren<TOccupants>(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in TOccupants occupants,
            scoped BucketPlan plan, in Fanout fanout, Span<NodeResult> results, scoped ReadOnlySpan<int> level,
            int branchDepth, ref BufferWriter writer, ref PbtNodeCluster.Builder cluster)
            where TOccupants : IOccupants, allows ref struct
        {
            ReadOnlySpan<int> bounds = level[..PbtWriteBatch.BoundsLength];
            uint touchedBitmask = (uint)level[PbtWriteBatch.TouchedMaskIndex];
            BoundaryScan scan = default;

            for (uint remainingBitmask = touchedBitmask | occupants.ChildSlotsBitmask; remainingBitmask != 0; remainingBitmask &= remainingBitmask - 1)
            {
                int slot = BitOperations.TrailingZeroCount(remainingBitmask);
                if ((touchedBitmask >> slot & 1) == 0)
                {
                    cluster.AppendChild(ref writer, occupants.ChildBlob(slot).Data);
                    continue;
                }

                Occupant occupant = occupants[slot];
                ref NodeResult result = ref results[slot];
                Span<PbtWriteBatch.StemEntry> bucket = entries[bounds[slot]..bounds[slot + 1]];
                TrieNodeKey childKey = key.ChildGroup(slot);
                BucketPlan childPlan = plan.ForChild(slot, branchDepth);
                bool childChanged;
                PbtSubtreeStats childDelta;
                if (occupant.Kind == NodeKind.Internal)
                {
                    ApplyGroup(childKey, bucket, occupants.ChildBlob(slot), occupant.NodeHash(), childPlan, fanout, ref writer, out result, out childChanged, out childDelta);
                }
                else if (occupant.Kind == NodeKind.Chain)
                {
                    ApplyChain(childKey, bucket, occupant.ChainData, childPlan, fanout, ref writer, out result, out childChanged, out childDelta);
                }
                else
                {
                    ApplyPushedStem(childKey, bucket, occupant, childPlan, fanout, ref writer, out result, out childChanged, out childDelta);
                }

                if (result.Kind == NodeKind.Internal) cluster.MarkChild(in writer);
                scan.Add(slot, result, childChanged, childDelta);
            }

            // a child this frame holds is under no key of its own, so none of them was read from the store
            return scan.ToShape(touchedBitmask, storedChildBitmask: 0);
        }

        /// <summary>
        /// <inheritdoc cref="ResolveBoundaries" path="/summary"/> For a frame whose children are stored
        /// under their own keys, each folding into a buffer of its own that this frame plants or removes.
        /// </summary>
        /// <remarks>
        /// The one frame that hands work to another thread, its children being the only ones that fold
        /// into a buffer of their own: a clustered frame's go straight into the blob it is assembling, in
        /// an order only it can keep. A bucket big enough to be worth the hand-off
        /// (<see cref="_minQueueEntries"/>) is handed to whichever thread reaches it first;
        /// everything else is folded here and now.
        /// </remarks>
        private GroupShape ResolveKeyedChildren<TOccupants>(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in TOccupants occupants,
            scoped BucketPlan plan, in Fanout fanout, Span<NodeResult> results, scoped ReadOnlySpan<int> level,
            int branchDepth)
            where TOccupants : IOccupants, allows ref struct
        {
            ReadOnlySpan<int> bounds = level[..PbtWriteBatch.BoundsLength];
            uint touchedBitmask = (uint)level[PbtWriteBatch.TouchedMaskIndex];
            uint storedChildBitmask = 0;
            BoundaryScan scan = default;

            // The buckets this frame handed out, which the settle below takes back whatever it can
            // still do itself.
            QueuedBuckets queued = default;
            bool mayQueue = MayQueue(touchedBitmask, in fanout);

            for (uint remainingBitmask = touchedBitmask; remainingBitmask != 0; remainingBitmask &= remainingBitmask - 1)
            {
                int slot = BitOperations.TrailingZeroCount(remainingBitmask);
                Occupant occupant = occupants[slot];
                Span<PbtWriteBatch.StemEntry> bucket = entries[bounds[slot]..bounds[slot + 1]];
                TrieNodeKey childKey = key.ChildGroup(slot);
                BucketPlan childPlan = plan.ForChild(slot, branchDepth);

                if (mayQueue && bucket.Length >= _minQueueEntries
                    && TryQueue(slot, childKey, bucket, occupant, childPlan, in fanout, ref queued))
                {
                    continue;
                }

                ref NodeResult result = ref results[slot];
                ApplyKeyedChild(childKey, bucket, occupant, childPlan, fanout, out result, out bool childChanged, out PbtSubtreeStats childDelta, out bool storedChild);
                if (storedChild) storedChildBitmask |= 1u << slot;
                scan.Add(slot, result, childChanged, childDelta);
            }

            if (!queued.IsEmpty) Settle(in fanout, ref queued, results, ref scan, ref storedChildBitmask);

            return scan.ToShape(touchedBitmask, storedChildBitmask);
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
            in TrieNodeKey childKey, Span<PbtWriteBatch.StemEntry> bucket, in Occupant occupant,
            scoped BucketPlan childPlan, in Fanout fanout, out NodeResult result, out bool changed,
            out PbtSubtreeStats delta, out bool storedChild)
        {
            BufferWriter owned = new(_memoryProvider);
            storedChild = false;
            if (occupant.Kind == NodeKind.Internal)
            {
                // a stored child group descends with its own content; the boundary internal caches its
                // old root hash, which the child no longer stores itself
                using RefCountingMemory? childData = _store.GetTrieNode(childKey);
                storedChild = childData is not null;
                ApplyClustered(childKey, bucket, StoredBlob.Of(childData), occupant.NodeHash(), childPlan, fanout, ref owned, out result, out changed, out delta);
                if (owned.Detach() is { } childBlob) result = result.WithBlob(childBlob);

                // No frame writes its own key; the parent settles each child's: a stored one the writes
                // emptied to nothing, or collapsed into a run the parent now holds, is removed here (a
                // child that produced a blob of its own is planted by the parent's rebuild).
                if (storedChild && changed && result.KeyedBlob is null) _store.SetTrieNode(childKey, null);
                return;
            }

            if (occupant.Kind == NodeKind.Chain)
            {
                ApplyChain(childKey, bucket, occupant.ChainData, childPlan, fanout, ref owned, out result, out changed, out delta);
            }
            else
            {
                ApplyPushedStem(childKey, bucket, occupant, childPlan, fanout, ref owned, out result, out changed, out delta);
            }

            if (owned.Detach() is { } blob) result = result.WithBlob(blob);
        }

        /// <summary>What the boundary walk adds up as it settles each slot, for the shape it hands the rebuild.</summary>
        private struct BoundaryScan
        {
            private uint _occupiedBitmask;
            private uint _stemsBitmask;
            private uint _chainsBitmask;
            private uint _changedBitmask;
            private PbtSubtreeStats _deltas;

            public void Add(int slot, in NodeResult result, bool changed, in PbtSubtreeStats delta)
            {
                Debug.Assert(changed || delta.IsZero, "an unchanged subtree is the same subtree, so it holds the same stemsBitmask");

                uint slotBitmask = 1u << slot;
                if (changed) _changedBitmask |= slotBitmask;
                if (result.Kind != NodeKind.Absent) _occupiedBitmask |= slotBitmask;
                if (result.Kind == NodeKind.Stem) _stemsBitmask |= slotBitmask;
                if (result.Kind == NodeKind.Chain) _chainsBitmask |= slotBitmask;
                _deltas += delta;
            }

            public readonly GroupShape ToShape(uint touchedBitmask, uint storedChildBitmask) =>
                new(new NodeGroupBitmasks(_occupiedBitmask, _stemsBitmask, _chainsBitmask), _changedBitmask, touchedBitmask, storedChildBitmask, _deltas);
        }

        /// <summary>
        /// Settles this frame's node from the boundary <paramref name="results"/> and their
        /// <paramref name="shape"/> that <see cref="ResolveBoundaries"/> produced — collapse to a run, hoist
        /// a stem, skip an unchanged group, or fold a fresh one — and persists each child's blob, returning
        /// the node now occupying this frame's key. An untouched slot is read back from
        /// <paramref name="occupants"/> on demand, bar what a seeded caller wrote into
        /// <paramref name="results"/> before the call because nothing can read it back later.
        /// </summary>
        /// <remarks>
        /// One call per group frame, from each of the three that resolve one:
        /// <list type="bullet">
        /// <item><see cref="ApplyGroup"/> — a stored group, passing its decoded encoding as <paramref name="existing"/>.</item>
        /// <item><see cref="ApplyPushedStem"/> — a frame holding at most a stem pushed down from the parent.</item>
        /// <item><see cref="ApplyChainSplit"/> — the group a run branches into, seeding what is left of the run.</item>
        /// </list>
        /// The last two seed their lone occupant into a frame with no stored group behind it, so both pass
        /// the empty <paramref name="existing"/> and write that occupant into <paramref name="results"/>
        /// themselves, there being no encoding here to read its boundary back out of.
        /// </remarks>
        /// <param name="occupants"><inheritdoc cref="ResolveBoundaries" path="/param[@name='occupants']"/></param>
        /// <param name="existing">The stored group encoding the frame descends from, or the empty group for a seeded frame.</param>
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
        /// <param name="cluster"><inheritdoc cref="ResolveBoundaries" path="/param[@name='cluster']"/></param>
        /// <param name="mark">
        /// Where this frame's bytes start in <paramref name="writer"/>, which it rolls back to when it
        /// settles into a node no key holds — a run, or a hoisting stem. Nothing written past it is a group
        /// above's, so nothing else is lost.
        /// </param>
        private NodeResult RebuildNode<TOccupants>(
            in TrieNodeKey key, in TOccupants occupants, in PbtTrieNodeGroup existing,
            Span<NodeResult> results, in GroupShape shape,
            in ValueHash256 beforeHash, in PbtSubtreeStats beforeStats, ref BufferWriter writer,
            ref PbtNodeCluster.Builder cluster, int mark, out bool changed, out PbtSubtreeStats delta)
            where TOccupants : IOccupants, allows ref struct
        {
            bool isRoot = key.Depth == 0;
            bool headsCluster = PbtLayout.IsClusteringDepth(key.Depth);
            NodeGroupBitmasks boundary = shape.Boundary;
            (uint occupiedBitmask, uint stemsBitmask, uint chainsBitmask) = boundary;
            uint changedBitmask = shape.ChangedBitmask;
            uint touchedBitmask = shape.TouchedBitmask;
            uint storedChildBitmask = shape.StoredChildBitmask;
            AssertUntouchedMerged(occupants, boundary, touchedBitmask);
            // What lets the cluster's offsets be the writer's own count: a group holding its children is
            // one no group above holds, so its bytes are the whole of the buffer rather than a slice of it.
            Debug.Assert(!headsCluster || mark == 0, "a clustering group's bytes must start the buffer for its child offsets to be absolute");
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
            if (rootKind == NodeKind.Internal && !isRoot && BitOperations.PopCount(occupiedBitmask) == 1)
            {
                int survivorSlot = BitOperations.TrailingZeroCount(occupiedBitmask);
                Debug.Assert(
                    occupants.HasStoredEncoding || results[survivorSlot].Kind != NodeKind.Absent,
                    "a seeded frame's occupant rides in results, there being no encoding to read it back out of");

                // an untouched survivor was never copied into results — read it straight from the occupant
                NodeResult survivor = results[survivorSlot].Kind != NodeKind.Absent
                    ? results[survivorSlot].Lease()
                    : AdoptOccupant(occupants[survivorSlot]);

                // A clustering frame's blob goes with the collapse, and the survivor's bytes with it: they
                // move out into memory of their own, to be planted under the survivor's own key. The lone
                // occupant is the only child there can be, so whatever the descent wrote is its encoding,
                // and where it wrote nothing the blob being replaced still holds it.
                if (headsCluster && survivor.Blob is null)
                {
                    survivor = AdoptChildBlob(
                        writer.WrittenCount != mark ? writer.WrittenSpan[mark..] : occupants.ChildBlob(survivorSlot).Data, survivor);
                }

                writer.Reset(mark);

                // Nothing is read to settle this: a survivor that is itself a run was in this very group's
                // encoding, so merging with it — which is what keeps runs maximal — needs no lookup,
                // however untouched it is. The fold to this frame's depth happens before the comparison:
                // unlike a stem's, a run's node hash is its path's, so it is only the same node once it
                // starts where beforeHash did.
                NodeResult chain = WrapIntoChain(
                    key.Depth, key.ChildGroup(survivorSlot), survivor, (storedChildBitmask >> survivorSlot & 1) != 0, afterStats);
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
                int hoistedSlot = rootKind == NodeKind.Absent ? -1 : BitOperations.TrailingZeroCount(occupiedBitmask);
                NodeResult hoisted = hoistedSlot < 0 ? default
                    : results[hoistedSlot].Kind != NodeKind.Absent ? results[hoistedSlot] : occupants[hoistedSlot];
                changed = hoisted.NodeHash() != beforeHash;
                Release(results, hoistedSlot);
                writer.Reset(mark);
                return hoisted;
            }

            // Hand the fold only the boundaries it cannot read back out of the existing encoding, in slot
            // order: the changed ones, and — when there is no existing group to read from (a run split or a
            // pushed stem seeds its occupants into an empty one) — every occupied slot. A changed slot that
            // emptied out contributes no boundary, so only the occupied ones go in.
            RefList16<(int Slot, Boundary Node)> boundaryBuffer = new(PbtLayout.TrieNodeGroupBoundarySlots);
            Span<(int Slot, Boundary Node)> changedBoundaries = boundaryBuffer.AsSpan();
            int changedCount = 0;
            uint gatherBitmask = occupiedBitmask & (occupants.HasStoredEncoding ? changedBitmask : ~0u);
            for (uint remainingBitmask = gatherBitmask; remainingBitmask != 0; remainingBitmask &= remainingBitmask - 1)
            {
                // ascending, as the fold requires
                int slot = BitOperations.TrailingZeroCount(remainingBitmask);
                Debug.Assert(
                    occupants.HasStoredEncoding || results[slot].Kind != NodeKind.Absent,
                    "a seeded frame has no encoding to read a boundary back out of, so every occupiedBitmask slot must ride in results");

                NodeResult node = results[slot];
                changedBoundaries[changedCount++] = (slot, new Boundary(
                    node.Hash,
                    (stemsBitmask >> slot & 1) != 0 ? node.Stem : default,
                    (chainsBitmask >> slot & 1) != 0 ? node.Blob : null));
            }

            // The children a clustering frame holds are already in the writer, the descent having folded or
            // copied each one in as it went, so all that is left is to close them off with their offset
            // table and fold this group in behind it. A group rooting none — every boundary a stem or a
            // run, whose bytes go into the group's own encoding — stores as a bare group whatever its
            // depth, there being nothing to cluster.
            bool holdsChildren = headsCluster && boundary.ChildSlots != 0;
            if (holdsChildren) cluster.WriteOffsets(ref writer);

            GroupRebuild rebuild = new(changedBoundaries[..changedCount], existing, boundary, changedBitmask, beforeHash, _writeFormat);
            (Stem? rootStem, ValueHash256 rootHash) = rebuild.Rebuild(ref writer, afterStats);
            NodeResult rootNode = rootStem is { } stem ? NodeResult.StemNode(stem, rootHash) : NodeResult.Internal(rootHash);

            if (holdsChildren) cluster.Finish(ref writer);

            // Unchanged root => the encoding is byte-identical to what is stored (an internal root whose
            // hash matches implies the same subtree, hence the same cached boundary hashes).
            changed = rootNode.NodeHash() != beforeHash;

            // Plant each child group's blob at its key. The frame that built it never wrote it; this one,
            // its parent, does. A clustering frame plants none of them: it has just written their bytes into
            // its own blob, and the keys they would go under hold nothing. A run is planted by neither: the
            // fold above has just written it into this group's own encoding.
            if (!headsCluster)
            {
                for (int slot = 0; slot < PbtLayout.TrieNodeGroupBoundarySlots; slot++)
                {
                    if ((changedBitmask >> slot & 1) != 0 && results[slot].KeyedBlob is { } childBlob)
                    {
                        childBlob.AcquireLease();
                        _store.SetTrieNode(key.ChildGroup(slot), childBlob);
                    }
                }
            }

            Release(results, handedUp: -1);

            return rootNode;
        }

        /// <summary>
        /// Copies a child group's bytes out of the cluster holding them into memory of its own, for an
        /// ancestor to plant under the key it is about to need again.
        /// </summary>
        /// <param name="node">What the frame read the child as, whose hash a group keeps.</param>
        private NodeResult AdoptChildBlob(ReadOnlySpan<byte> childBlob, in NodeResult node)
        {
            Debug.Assert(!childBlob.IsEmpty, "a boundary internal roots a blob, which a clustering frame holds itself");

            RefCountingMemory memory = _memoryProvider.Rent(childBlob.Length);
            childBlob.CopyTo(memory.GetSpan());
            return NodeResult.Internal(node.Hash, memory);
        }

        /// <summary>
        /// Promotes an untouched occupant to a result the frame can hand up, copying a run out of the
        /// encoding it sits in — which the frame reading it is about to replace — into memory of its own.
        /// </summary>
        private NodeResult AdoptOccupant(in Occupant occupant) =>
            occupant.Kind == NodeKind.Chain ? CopyChainNode(occupant.ChainData) : occupant;

        /// <summary>
        /// Releases the leases <paramref name="nodes"/> hold once a frame is done with them, bar slot
        /// <paramref name="handedUp"/> — the node it returns, whose lease goes with it (<c>-1</c> when
        /// it returns a node of its own).
        /// </summary>
        private static void Release<T>(Span<T> nodes, int handedUp) where T : struct, IDisposable
        {
            for (int slot = 0; slot < nodes.Length; slot++)
            {
                if (slot != handedUp) nodes[slot].Dispose();
            }
        }

        // Internal rather than private, this and the two structs below, only so that BucketJob — which
        // Updater has to name, and so cannot be private — may carry them. Naming any of them still means
        // naming Updater, which is private, so nothing outside this class sees them either way.
        internal readonly record struct NodeRef(NodeKind Kind, int Offset);

        /// <summary>
        /// A node already sitting in a boundary slot as the descent begins: a reference into its encoding
        /// and a lease on the memory holding it. Read out of an existing group's blob, or synthesised — a
        /// run, a boundary pointer — to seed a frame. An absent occupant holds nothing, so <c>default</c> is one.
        /// </summary>
        /// <remarks>
        /// The descent's input, distinct from its output <see cref="NodeResult"/>. An occupant is always
        /// backed by an encoding, so it never carries a by-value hash; it promotes to a result — a stored
        /// node is a valid one — through the implicit conversion below.
        /// </remarks>
        internal readonly struct Occupant(NodeRef node, RefCountingMemory? memory) : IDisposable
        {
            private readonly NodeRef _node = node;
            private readonly RefCountingMemory? _memory = memory;

            /// <summary>
            /// Promotes an occupant to a result. A stem or internal, already persisted where it sits,
            /// becomes a pure value with no blob of its own; a run carries the memory it was seeded in,
            /// which is why only <see cref="AdoptOccupant"/> promotes one read out of a group's encoding.
            /// </summary>
            public static implicit operator NodeResult(Occupant occupant) => occupant.Kind switch
            {
                NodeKind.Chain => NodeResult.Chain(occupant.SeedMemory),
                NodeKind.Stem => NodeResult.StemNode(occupant.Stem, occupant.Hash),
                NodeKind.Internal => NodeResult.Internal(occupant.Hash),
                _ => default,
            };

            public NodeKind Kind => _node.Kind;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Stem"/>
            public Stem Stem => Slot.Stem;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.ChainData"/>
            public ReadOnlySpan<byte> ChainData => Slot.ChainData;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Hash"/>
            public ValueHash256 Hash => Slot.Hash;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.NodeHash"/>
            public ValueHash256 NodeHash() => Slot.NodeHash();

            /// <summary>The whole of the memory behind a seeded run, which its result takes over.</summary>
            private RefCountingMemory SeedMemory
            {
                get
                {
                    Debug.Assert(_node.Offset == 0, "a run read out of a group's encoding is no encoding of its own");
                    return _memory!;
                }
            }

            private PbtTrieNodeGroup.Slot Slot =>
                _memory is null ? default : PbtTrieNodeGroup.SlotAt(_memory.GetSpan(), _node.Kind, _node.Offset);

            public void Dispose() => ((IDisposable?)_memory)?.Dispose();
        }

        /// <summary>
        /// The result of applying a batch to a slot: the node now occupying it, handed up for a parent to
        /// place. Usually backed by an encoding, but the folded internal group root has no entry, so it is
        /// carried by value instead — see <see cref="Unstored"/>.
        /// </summary>
        /// <remarks>
        /// The descent's output, distinct from its input <see cref="Occupant"/>. A group's encoding is not
        /// among what it carries: the frame folded that into a <see cref="BufferWriter"/>, so it is either
        /// the blob of the group above already, or a buffer <see cref="BufferWriter.Detach"/> hands over for the parent
        /// to plant. What a result does hold is a node with nowhere else to live — a run whose
        /// <see cref="PbtNodeChain"/> encoding no group holds until an ancestor writes it into its own, or a
        /// child group's bytes moved out of a cluster that is collapsing — and the lease keeps it safe to
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

        /// <summary>The leaf blob layout that goes with the group format; one setting picks how far both skip.</summary>
        private PbtLeafFormat LeafFormat => _writeFormat switch
        {
            PbtGroupFormat.Interleaved => PbtLeafFormat.Interleaved,
            PbtGroupFormat.BoundaryOnly => PbtLeafFormat.LeavesOnly,
            _ => PbtLeafFormat.EveryLevel,
        };

        /// <summary>Folds one stem's writes (<paramref name="changes"/>) into its leaf blob, persists it, and reports whether the stem is now empty.</summary>
        /// <param name="knownAbsent">
        /// The stem had no node in the trie, so — a stem node and its leaf blob being born and dying
        /// together, keyed by the stem — there is no stored blob to merge and the read is skipped.
        /// </param>
        private bool ComputeBlob(in Stem stem, IPbtStemChanges changes, bool knownAbsent, out ValueHash256 subtreeRoot)
        {
            using RefCountingMemory? prior = knownAbsent ? null : _store.GetLeafBlob(stem);
            using StemLeafBlob.RebuildState newBlob = StemLeafBlob.Apply(prior is null ? default : prior.GetSpan(), changes, _memoryProvider, LeafFormat);
            subtreeRoot = newBlob.SubtreeRoot;
            bool isEmpty = newBlob.IsEmpty;
            _store.SetLeafBlob(stem, newBlob.Take());
            return isEmpty;
        }

        /// <summary>
        /// Checks that the caller folded its untouched slots back into the shape with
        /// <see cref="GroupShape.MergeUntouched"/>: off the touched slots, the shape must say exactly what
        /// the occupants do.
        /// </summary>
        /// <remarks>
        /// <see cref="ResolveBoundaries"/> reports only what it descended, so an omitted merge leaves the
        /// rebuild blind to every untouched child — a wrong fold, and a wrong state root. It shows up at
        /// once for a stored group, whose untouched slots are usually many; a seeded frame has one slot
        /// that is often touched anyway, so there it could pass unnoticed.
        /// </remarks>
        [Conditional("DEBUG")]
        private static void AssertUntouchedMerged<TOccupants>(in TOccupants occupants, NodeGroupBitmasks boundary, uint touchedBitmask)
            where TOccupants : IOccupants, allows ref struct
        {
            for (uint untouchedBitmask = ~touchedBitmask & ((1u << PbtLayout.TrieNodeGroupBoundarySlots) - 1); untouchedBitmask != 0; untouchedBitmask &= untouchedBitmask - 1)
            {
                int slot = BitOperations.TrailingZeroCount(untouchedBitmask);
                NodeKind kind = occupants[slot].Kind;

                // a constant message: Debug.Assert evaluates its argument on every call, and this one runs
                // per untouched slot of every frame
                Debug.Assert(
                    (boundary.Presence >> slot & 1) != 0 == (kind != NodeKind.Absent)
                    && (boundary.Stems >> slot & 1) != 0 == (kind == NodeKind.Stem)
                    && (boundary.Chains >> slot & 1) != 0 == (kind == NodeKind.Chain),
                    "an untouchedBitmask slot disagrees with its occupant — the caller skipped GroupShape.MergeUntouched");
            }
        }

        /// <summary>
        /// Checks that whoever bucketed the range cached the same touched mask its bounds imply.
        /// </summary>
        /// <remarks>
        /// The mask crosses a format boundary — <c>PbtWriteBatchBuilder</c> fills it for a precalculated
        /// level, <see cref="Partition"/> for one bucketed here — so nothing but this re-derivation would
        /// catch the two drifting apart.
        /// </remarks>
        [Conditional("DEBUG")]
        private static void AssertTouchedMaskMatchesBounds(uint touchedBitmask, ReadOnlySpan<int> bounds)
        {
            uint derived = 0;
            for (int slot = 0; slot < PbtLayout.TrieNodeGroupBoundarySlots; slot++)
            {
                if (bounds[slot] != bounds[slot + 1]) derived |= 1u << slot;
            }

            Debug.Assert(touchedBitmask == derived, "the cached touched mask disagrees with its level's bounds");
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
        /// entry count alone rather than walking the range again per level. Unlike the bounds it survives a
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
        private readonly ref struct BucketPlan(ReadOnlySpan<int> precalculated, int branchDepth)
        {
            /// <inheritdoc cref="BucketPlan" path="/param[@name='precalculated']"/>
            public ReadOnlySpan<int> Precalculated { get; } = precalculated;

            /// <inheritdoc cref="BucketPlan" path="/param[@name='branchDepth']"/>
            public int BranchDepth { get; } = branchDepth;

            /// <summary>
            /// The plan for the child group under boundary slot <paramref name="slot"/>, whose range this
            /// frame's bucketing found to part at <paramref name="branchDepth"/>.
            /// </summary>
            /// <remarks>
            /// The precalculated levels are laid out coarsest-last, each one
            /// <see cref="PbtWriteBatch.LevelStride"/> wide per group it describes, so the finer levels of a
            /// slot's subtree are the slice at its own index — and what remains after this group's own level
            /// divides evenly among the sixteen slots.
            /// </remarks>
            public BucketPlan ForChild(int slot, int branchDepth)
            {
                ReadOnlySpan<int> buckets = Precalculated;
                int childLength = buckets.Length <= PbtWriteBatch.LevelStride
                    ? 0
                    : (buckets.Length - PbtWriteBatch.LevelStride) / PbtLayout.TrieNodeGroupBoundarySlots;
                return new BucketPlan(childLength == 0 ? default : buckets.Slice(slot * childLength, childLength), branchDepth);
            }

            /// <summary>The plan a frame deeper down the same range inherits, the levels skipped over describing neither depth.</summary>
            public BucketPlan AfterJump() => new(default, BranchDepth);
        }

        /// <summary>
        /// Fills <paramref name="level"/> for a range that falls wholly in bucket <paramref name="nibble"/>,
        /// in the shape <see cref="Partition"/> would have produced for it.
        /// </summary>
        private static void FillSingleBucket(int nibble, int count, Span<int> level)
        {
            Span<int> bounds = level[..PbtWriteBatch.BoundsLength];
            bounds[..(nibble + 1)].Clear();
            bounds[(nibble + 1)..].Fill(count);
            level[PbtWriteBatch.TouchedMaskIndex] = 1 << nibble;
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
        private static void AssertBranchDepthSound(ReadOnlySpan<PbtWriteBatch.StemEntry> entries, int depth, int claimed)
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
                claimed <= (splitDepth & ~(PbtLayout.TrieNodeGroupLevelsPerGroup - 1)),
                "the inherited branch depth reaches past where the range parts");
        }

        /// <summary>
        /// Radix-partitions <paramref name="entries"/> (sharing bits <c>[0, depth)</c>, any order) in place
        /// into the sixteen boundary buckets of the group at <paramref name="depth"/>, keyed by the four
        /// stem bits at that depth: bucket i is <paramref name="level"/>[i]..[i+1]. Mirrors
        /// <c>PatriciaTree.BulkSet</c>'s per-level bucket sort — no global sort is needed, and within-bucket
        /// order is arbitrary since each bucket is re-partitioned by its child group.
        /// </summary>
        /// <param name="level">
        /// One <see cref="PbtWriteBatch.LevelStride"/>-wide level, filled with the bounds and the
        /// <see cref="PbtWriteBatch.TouchedMaskIndex"/> mask, in the same shape a precalculated level
        /// arrives in.
        /// </param>
        /// <returns>
        /// The depth of the group where <paramref name="entries"/> first part from one another: this
        /// group's own whenever two of them fall in different buckets, and a deeper one when they all
        /// share its nibble — the levels between hold a single child apiece, so a caller free to skip
        /// them can jump straight to the group that branches.
        /// </returns>
        private static int Partition(Span<PbtWriteBatch.StemEntry> entries, int depth, Span<int> level)
        {
            Span<int> bounds = level[..PbtWriteBatch.BoundsLength];
            // Only the levels the producer left unbucketed reach here, which for a batch drained from
            // PbtWriteBatchBuilder — its shard key handing over the depth-0 and depth-4 bounds — is
            // everything below depth 8, by which point the batch has fragmented sixteen ways twice and a
            // range is almost always one or a few entries. The count-and-permute below costs its
            // sixteen-wide passes whether it buckets one entry or a hundred, so the tiny ranges that
            // dominate take a sort network instead.
            if (entries.Length <= TinyRange) return SortTiny(entries, depth, level);

            Span<int> counts = stackalloc int[PbtLayout.TrieNodeGroupBoundarySlots];
            counts.Clear();

            // The pass that buckets the range doubles as the search for where it parts, both wanting the
            // same walk over the same stems: a stem's bucket is its nibble here, and the range's shared
            // prefix is the shortest any of them shares with a fixed member of it. Once one entry parts
            // inside this group's own four levels there is no deeper jump left to find, so the search
            // falls away and leaves the count to run on alone.
            Stem reference = entries[0].Stem;
            int floor = depth + PbtLayout.TrieNodeGroupLevelsPerGroup;
            int splitDepth = Stem.LengthInBits;
            for (int i = 0; i < entries.Length; i++)
            {
                Stem stem = entries[i].Stem;
                counts[NibbleOf(stem, depth)]++;
                if (splitDepth > floor)
                {
                    // FirstDifferingBit reports -1 for a stem that never parts — the reference against
                    // itself, or a duplicate — which as an unsigned compare reads as past everything, which
                    // is what never parting means, as it does in ApplyChain.
                    int diff = stem.FirstDifferingBit(reference, depth);
                    if ((uint)diff < (uint)splitDepth) splitDepth = diff;
                }
            }

            int total = 0;
            uint touched = 0;
            for (int bucket = 0; bucket < PbtLayout.TrieNodeGroupBoundarySlots; bucket++)
            {
                bounds[bucket] = total;
                if (counts[bucket] != 0) touched |= 1u << bucket;
                total += counts[bucket];
            }
            bounds[PbtLayout.TrieNodeGroupBoundarySlots] = total;
            level[PbtWriteBatch.TouchedMaskIndex] = (int)touched;
            Debug.Assert(total == entries.Length);

            // One populated bucket holds every entry already, whatever their order, so the permutation
            // below is a no-op that still costs its head array and its sixteen-wide walk.
            if (BitOperations.IsPow2(touched))
            {
                Debug.Assert(splitDepth >= floor, "one populated bucket means nothing parts within this group");
                return splitDepth & ~(PbtLayout.TrieNodeGroupLevelsPerGroup - 1);
            }

            // In-place American-flag permutation: each swap places one entry into its final bucket.
            Span<int> heads = stackalloc int[PbtLayout.TrieNodeGroupBoundarySlots];
            bounds[..PbtLayout.TrieNodeGroupBoundarySlots].CopyTo(heads);
            for (int bucket = 0; bucket < PbtLayout.TrieNodeGroupBoundarySlots; bucket++)
            {
                while (heads[bucket] < bounds[bucket + 1])
                {
                    int target = NibbleOf(entries[heads[bucket]].Stem, depth);
                    if (target == bucket)
                    {
                        heads[bucket]++;
                    }
                    else
                    {
                        (entries[heads[bucket]], entries[heads[target]]) = (entries[heads[target]], entries[heads[bucket]]);
                        heads[target]++;
                    }
                }
            }

            return depth;
        }

        /// <summary>
        /// <inheritdoc cref="Partition" path="/summary"/> For a range of at most <see cref="TinyRange"/>
        /// entries, which a sorting network on the nibble orders in as many compare-and-swaps.
        /// </summary>
        /// <remarks>
        /// Mirrors <c>PatriciaTree.BulkSet</c>'s <c>SortTiny</c>. Ordering the nibbles makes every bucket
        /// a contiguous run, so the bounds fill as a handful of vectorized spans rather than a per-slot
        /// loop over a counts array.
        /// </remarks>
        private static int SortTiny(Span<PbtWriteBatch.StemEntry> entries, int depth, Span<int> level)
        {
            Debug.Assert(!entries.IsEmpty && entries.Length <= TinyRange);

            Span<int> bounds = level[..PbtWriteBatch.BoundsLength];

            Span<int> nibbleBuffer = stackalloc int[TinyRange];
            Span<int> nibbles = nibbleBuffer[..entries.Length];

            // Read before the network below reorders them, which the search is indifferent to: the range
            // shares whatever the shortest prefix any of them shares with a fixed member is, whichever
            // member that is.
            Stem reference = entries[0].Stem;
            int splitDepth = Stem.LengthInBits;
            uint touched = 0;
            for (int i = 0; i < nibbles.Length; i++)
            {
                Stem stem = entries[i].Stem;
                nibbles[i] = NibbleOf(stem, depth);
                touched |= 1u << nibbles[i];
                int diff = stem.FirstDifferingBit(reference, depth);
                if ((uint)diff < (uint)splitDepth) splitDepth = diff;
            }

            level[PbtWriteBatch.TouchedMaskIndex] = (int)touched;

            if (nibbles.Length > 1)
            {
                CompareAndSwap(entries, nibbles, 0, 1);
                if (nibbles.Length > 2)
                {
                    CompareAndSwap(entries, nibbles, 1, 2);
                    CompareAndSwap(entries, nibbles, 0, 1);
                }
            }

            // Ascending nibbles leave bounds[i] — the number of entries whose nibble is below i — stepping
            // up only where one sits, so each run holds the count so far. Entries sharing a nibble make the
            // run between them empty, which Fill ignores.
            bounds[..(nibbles[0] + 1)].Clear();
            for (int i = 1; i < nibbles.Length; i++) bounds[(nibbles[i - 1] + 1)..(nibbles[i] + 1)].Fill(i);
            bounds[(nibbles[^1] + 1)..].Fill(nibbles.Length);

            // Parting inside this group's own four levels rounds back down to it, which is what says the
            // range branches here and there is nothing to jump over.
            return splitDepth & ~(PbtLayout.TrieNodeGroupLevelsPerGroup - 1);
        }

        private static void CompareAndSwap(Span<PbtWriteBatch.StemEntry> entries, Span<int> nibbles, int i, int j)
        {
            if (nibbles[i] <= nibbles[j]) return;
            (entries[i], entries[j]) = (entries[j], entries[i]);
            (nibbles[i], nibbles[j]) = (nibbles[j], nibbles[i]);
        }

        private static int NibbleOf(in Stem stem, int depth) =>
            (depth & 4) == 0 ? stem.Bytes[depth >> 3] >> 4 : stem.Bytes[depth >> 3] & 0xF;
    }
}
