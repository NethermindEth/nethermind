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
    /// Which encoding to write the groups this batch rebuilds in. Both fold to the same root and both
    /// are read whatever this says, so it may change between batches over one store: a group is
    /// converted only by a change that rewrites it anyway.
    /// </param>
    public static ValueHash256 UpdateRoot(
        IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch changes, IRefCountingMemoryProvider memoryProvider,
        PbtGroupFormat writeFormat) =>
        changes.Count == 0 ? currentRoot : new Updater(store, memoryProvider, writeFormat).Run(currentRoot, changes);

    private sealed partial class Updater(IPbtStore store, IRefCountingMemoryProvider memoryProvider, PbtGroupFormat writeFormat)
    {
        /// <summary>The largest range <see cref="SortTiny"/> takes off <see cref="Partition"/>'s hands.</summary>
        private const int TinyRange = 3;

        /// <summary>
        /// What a boundary result turned out to be. The first three mirror <see cref="NodeKind"/>;
        /// <see cref="Chain"/> is a node stored as a <see cref="PbtNodeChain"/> blob of its own rather
        /// than inside a group — an internal node as far as the trie is concerned, which is why
        /// <see cref="KindOf"/> never produces it.
        /// </summary>
        private enum ResultKind : byte
        {
            Absent = NodeKind.Absent,
            Internal = NodeKind.Internal,
            Stem = NodeKind.Stem,
            Chain = 3,
        }

        public ValueHash256 Run(in ValueHash256 currentRoot, PbtWriteBatch changes)
        {
            // No global sort: each group radix-partitions its own range in place during the descent,
            // bar the levels the producer already bucketed for it.
            using RefCountingMemory? rootData = store.GetTrieNode(TrieNodeKey.Root);
            NodeResult root = default;
            ApplyStored(TrieNodeKey.Root, changes.Entries, rootData, currentRoot, changes.Buckets, ref root, out _, out _);

            using (root)
            {
                // The root has no parent to persist it, so the top frame does: plant its rebuilt blob, or
                // remove the key when the tree has emptied out.
                if (root.Blob is { } rootBlob)
                {
                    rootBlob.AcquireLease();
                    store.SetTrieNode(TrieNodeKey.Root, rootBlob);
                }
                else if (root.Kind == ResultKind.Absent && rootData is not null)
                {
                    store.SetTrieNode(TrieNodeKey.Root, null);
                }

                return root.NodeHash();
            }
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to whatever is stored at <paramref name="key"/>, which the
        /// two blob formats' disjoint leading bytes tell apart.
        /// </summary>
        /// <param name="existingData"><inheritdoc cref="ApplyGroup" path="/param[@name='existingData']"/></param>
        /// <param name="beforeHash"><inheritdoc cref="RebuildNode" path="/param[@name='beforeHash']"/></param>
        /// <param name="precalculatedBuckets"><inheritdoc cref="ResolveBoundaries" path="/param[@name='precalculatedBuckets']"/></param>
        /// <param name="result">The node now occupying the group's root position, written straight into the caller's slot.</param>
        /// <param name="changed"><inheritdoc cref="RebuildNode" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="RebuildNode" path="/param[@name='delta']"/></param>
        private void ApplyStored(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, RefCountingMemory? existingData,
            in ValueHash256 beforeHash, ReadOnlySpan<int> precalculatedBuckets, ref NodeResult result, out bool changed, out PbtSubtreeStats delta)
        {
            if (existingData is not null && PbtNodeChain.IsChain(existingData.GetSpan()))
                ApplyChain(key, entries, existingData.GetSpan(), precalculatedBuckets, ref result, out changed, out delta);
            else
                ApplyGroup(key, entries, existingData, beforeHash, precalculatedBuckets, ref result, out changed, out delta);
        }

        /// <summary>The kind a group's encoding gives a node, as a boundary result.</summary>
        private static ResultKind ToResultKind(NodeKind kind) => (ResultKind)kind;

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
        }

        /// <summary>The occupants of a stored group, each read straight out of its encoding at the slot's fixed boundary position.</summary>
        private readonly ref struct StoredOccupants(PbtTrieNodeGroup existing, RefCountingMemory? existingData) : IOccupants
        {
            private readonly PbtTrieNodeGroup _existing = existing;
            private readonly RefCountingMemory? _existingData = existingData;

            public bool HasStoredEncoding => !_existing.IsEmpty;

            public Occupant this[int slot]
            {
                get
                {
                    int position = PbtTrieNodeGroup.BoundaryPosition(slot);
                    NodeKind kind = _existing.KindAt(position);
                    return kind == NodeKind.Absent ? default : new Occupant(new NodeRef(ToResultKind(kind), _existing.EntryOffset(position)), _existingData);
                }
            }
        }

        /// <summary>The lone occupant a run-split or pushed stem seeds into an otherwise empty frame, held at its slot.</summary>
        private readonly struct SeededOccupant(Occupant seed, int seedSlot) : IOccupants
        {
            private readonly Occupant _seed = seed;
            private readonly int _seedSlot = seedSlot;

            public bool HasStoredEncoding => false;

            public Occupant this[int slot] => slot == _seedSlot ? _seed : default;
        }

        /// <summary>The kind a group's encoding stores a node under; a chain has none, living in no group.</summary>
        private static NodeKind ToNodeKind(ResultKind kind)
        {
            Debug.Assert(kind != ResultKind.Chain, "a chain node lives in no group's encoding");
            return (NodeKind)kind;
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
        /// The group's stored encoding, which the caller owns and this keeps no lease on beyond the
        /// call: the occupants read out of it — and the root, when the writes leave the group as it
        /// was — take their own.
        /// </param>
        /// <param name="beforeHash"><inheritdoc cref="RebuildNode" path="/param[@name='beforeHash']"/></param>
        /// <param name="precalculatedBuckets"><inheritdoc cref="ResolveBoundaries" path="/param[@name='precalculatedBuckets']"/></param>
        /// <param name="result"><inheritdoc cref="ApplyStored" path="/param[@name='result']"/></param>
        /// <param name="changed"><inheritdoc cref="RebuildNode" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="RebuildNode" path="/param[@name='delta']"/></param>
        private void ApplyGroup(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, RefCountingMemory? existingData,
            in ValueHash256 beforeHash, ReadOnlySpan<int> precalculatedBuckets, ref NodeResult result, out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty && depth % PbtTrieNodeGroup.LevelsPerGroup == 0);
            Debug.Assert(depth <= PbtTrieNodeGroup.MaxGroupDepth);

            PbtTrieNodeGroup existing = existingData is null ? default : PbtTrieNodeGroup.Decode(existingData.GetSpan());

            // The group's boundary shape, gathered straight out of its position bitmaps. The occupants
            // themselves the descent reads on demand out of `existing`, so no buffer is built here.
            existing.BoundaryShape(out uint occupantsOccupied, out uint occupantsStems);

            // Resolve the touched slots into a shared buffer, then rebuild the group's node from it.
            RefList16<NodeResult> resultBuffer = new(PbtTrieNodeGroup.BoundarySlots);
            Span<NodeResult> results = resultBuffer.AsSpan();

            StoredOccupants occupants = new(existing, existingData);
            GroupShape shape = ResolveBoundaries(key, entries, occupants, precalculatedBuckets, results)
                .MergeUntouched(occupantsOccupied, occupantsStems);
            result = RebuildNode(key, occupants, existing, results, shape, beforeHash, existing.Stats, out changed, out delta);
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to a subtree with no stored group at <paramref name="key"/>:
        /// either empty, or holding a single stem <paramref name="pushed"/> down from the parent's
        /// boundary slot. A range holding one uncontended stem has reached the shortest prefix no other
        /// stem shares, so that stem's node is built here, out of its leaf blob, without descending;
        /// otherwise the pushed stem and the writes are routed on down together.
        /// </summary>
        /// <param name="precalculatedBuckets"><inheritdoc cref="ResolveBoundaries" path="/param[@name='precalculatedBuckets']"/></param>
        /// <param name="result"><inheritdoc cref="ApplyStored" path="/param[@name='result']"/></param>
        /// <param name="changed"><inheritdoc cref="RebuildNode" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="RebuildNode" path="/param[@name='delta']"/></param>
        private void ApplyPushedStem(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in Occupant pushed,
            ReadOnlySpan<int> precalculatedBuckets, ref NodeResult result, out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty && depth % PbtTrieNodeGroup.LevelsPerGroup == 0);
            // a chain routes to ApplyChain instead: it is a whole subtree, not a node to place, and the
            // collapse below would drop it
            Debug.Assert(pushed.Kind is ResultKind.Absent or ResultKind.Stem);

            // Whatever was pushed down is the whole of what is here, so a lone stem is the only stem
            // this frame's subtree can hold before the writes.
            PbtSubtreeStats beforeStats = pushed.Kind == ResultKind.Stem ? PbtSubtreeStats.OneStem : default;

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
            if (entries.Length == 1 && (pushed.Kind == ResultKind.Absent || pushed.Stem == stem))
            {
                // a blob left with no leaves deletes its stem, so the node goes absent rather than up
                bool isEmpty = ComputeBlob(stem, entries[0].Changes, out ValueHash256 subtreeRoot);
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

            Debug.Assert(depth <= PbtTrieNodeGroup.MaxGroupDepth);

            uint occupantsOccupied = 0;
            int seedSlot = -1;
            if (pushed.Kind == ResultKind.Stem)
            {
                seedSlot = NibbleOf(pushed.Stem, depth);
                occupantsOccupied = 1u << seedSlot;
            }

            // Resolve the touched slots into a shared buffer, then rebuild the group's node from it.
            SeededOccupant occupants = new(pushed, seedSlot);
            RefList16<NodeResult> resultBuffer = new(PbtTrieNodeGroup.BoundarySlots);
            Span<NodeResult> results = resultBuffer.AsSpan();

            GroupShape shape = ResolveBoundaries(key, entries, occupants, precalculatedBuckets, results)
                .MergeUntouched(occupantsOccupied, occupantsOccupied);
            // A frame with no stored encoding leaves the rebuild nothing but `results` to read a boundary
            // out of, so the pushed stem rides on in its slot unless the descent already refreshed it. It
            // promotes to a pure value, so it carries no lease.
            if (seedSlot >= 0 && (shape.TouchedMask >> seedSlot & 1) == 0) results[seedSlot] = pushed;

            result = RebuildNode(key, occupants, default, results, shape, pushed.NodeHash(), beforeStats, out changed, out delta);
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
            uint Occupied, uint Stems, uint ChangedMask, uint TouchedMask, uint StoredChildMask, PbtSubtreeStats Deltas)
        {
            /// <summary>
            /// Folds the slots the descent never visited back in, from the shape the frame started with.
            /// </summary>
            /// <remarks>
            /// The descent's own bits are a subset of <see cref="TouchedMask"/>, so the arguments contribute
            /// only outside it and the two halves never collide.
            /// </remarks>
            public GroupShape MergeUntouched(uint untouchedOccupied, uint untouchedStems) => this with
            {
                Occupied = Occupied | (untouchedOccupied & ~TouchedMask),
                Stems = Stems | (untouchedStems & ~TouchedMask),
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
        /// <param name="precalculatedBuckets">
        /// The bucket table levels the producer already partitioned <paramref name="entries"/> into, this
        /// group's own being the last of them (<see cref="PbtWriteBatch.BucketTableLength"/>); empty once
        /// the descent runs past them, from where each group partitions its range itself.
        /// </param>
        private GroupShape ResolveBoundaries<TOccupants>(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in TOccupants occupants,
            ReadOnlySpan<int> precalculatedBuckets, Span<NodeResult> results)
            where TOccupants : IOccupants, allows ref struct
        {
            int depth = key.Depth;

            // A precalculated level is already a bounds array over this very range, so it is read where it
            // lies; only a range the producer left unbucketed is partitioned here.
            Span<int> computed = stackalloc int[PbtWriteBatch.LevelStride];
            scoped ReadOnlySpan<int> level;
            if (precalculatedBuckets.IsEmpty)
            {
                Partition(entries, depth, computed);
                level = computed;
            }
            else
            {
                level = precalculatedBuckets[^PbtWriteBatch.LevelStride..];
            }

            ReadOnlySpan<int> bounds = level[..PbtWriteBatch.BoundsLength];

            // Only the slots below get a bit set, so what comes back describes the descent and nothing else.
            uint occupied = 0;
            uint stems = 0;

            // The non-empty buckets, cached by whoever bucketed the range — the descent visits just these,
            // and an empty bucket leaves its slot's boundaries where they are.
            uint touchedMask = (uint)level[PbtWriteBatch.TouchedMaskIndex];
            Debug.Assert(touchedMask == DeriveTouchedMask(bounds), "the cached touched mask disagrees with its level's bounds");

            uint changedMask = 0;
            uint storedChildMask = 0;
            PbtSubtreeStats childDeltas = default;
            for (uint touched = touchedMask; touched != 0; touched &= touched - 1)
            {
                int slot = BitOperations.TrailingZeroCount(touched);
                Occupant occupant = occupants[slot];
                ref NodeResult result = ref results[slot];
                Span<PbtWriteBatch.StemEntry> bucket = entries[bounds[slot]..bounds[slot + 1]];
                TrieNodeKey childKey = key.ChildGroup(slot);
                ReadOnlySpan<int> childBuckets = ChildBuckets(precalculatedBuckets, slot);
                bool childChanged;
                PbtSubtreeStats childDelta;
                if (occupant.Kind == ResultKind.Internal)
                {
                    // a stored child blob — group or run — descends with its own content; the boundary
                    // internal caches its old root hash, which the child no longer stores itself
                    using RefCountingMemory? childData = store.GetTrieNode(childKey);
                    if (childData is not null) storedChildMask |= 1u << slot;
                    ApplyStored(childKey, bucket, childData, occupant.NodeHash(), childBuckets, ref result, out childChanged, out childDelta);

                    // No frame writes its own key; this one, the parent, settles each child's: a stored one
                    // the writes emptied to nothing — it changed and holds no blob now — is removed here (a
                    // child that produced a new blob is planted by the rebuild below instead).
                    if (childData is not null && childChanged && result.Blob is null) store.SetTrieNode(childKey, null);
                }
                else if (occupant.Kind == ResultKind.Chain)
                {
                    // the run ApplyChainSplit seeded below itself, which no key holds
                    ApplyChain(childKey, bucket, occupant.ChainData, childBuckets, ref result, out childChanged, out childDelta);
                }
                else
                {
                    // an empty subtree or a lone stem: push the occupant (stem or absent) on down
                    ApplyPushedStem(childKey, bucket, occupant, childBuckets, ref result, out childChanged, out childDelta);
                }

                if (childChanged) changedMask |= 1u << slot;
                Debug.Assert(childChanged || childDelta.IsZero, "an unchanged subtree is the same subtree, so it holds the same stems");
                childDeltas += childDelta;

                // the kind the write left here, over the bits the bulk clear above already dropped
                uint bit = 1u << slot;
                ResultKind resultKind = result.Kind;
                if (resultKind != ResultKind.Absent) occupied |= bit;
                if (resultKind == ResultKind.Stem) stems |= bit;
            }

            return new GroupShape(occupied, stems, changedMask, touchedMask, storedChildMask, childDeltas);
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
        private NodeResult RebuildNode<TOccupants>(
            in TrieNodeKey key, in TOccupants occupants, in PbtTrieNodeGroup existing,
            Span<NodeResult> results, in GroupShape shape,
            in ValueHash256 beforeHash, in PbtSubtreeStats beforeStats, out bool changed, out PbtSubtreeStats delta)
            where TOccupants : IOccupants, allows ref struct
        {
            bool isRoot = key.Depth == 0;
            uint occupied = shape.Occupied;
            uint stems = shape.Stems;
            uint changedMask = shape.ChangedMask;
            uint touchedMask = shape.TouchedMask;
            uint storedChildMask = shape.StoredChildMask;
            AssertUntouchedMerged(occupants, occupied, stems, touchedMask);
            NodeKind rootKind = KindOf(occupied, stems);

            // Every stem of this subtree sits under one of the sixteen slots, so what they hoisted is the
            // whole of what changed here. This holds however the nodes are settled below: the statistics
            // count the subtree's content, and none of removing, collapsing or rebuilding moves a stem.
            delta = shape.Deltas;
            PbtSubtreeStats afterStats = beforeStats + shape.Deltas;

            // A group left with one internal child is a run of single-child levels: five nodes that all
            // change whenever its one leaf does, and that nothing reads on their own. It stores as a
            // PbtNodeChain instead, merged with any run below so that a run is always as long as it can be.
            // The root group is exempt, as it is for a lone stem: it has no parent to hoist into.
            if (rootKind == NodeKind.Internal && !isRoot && BitOperations.PopCount(occupied) == 1)
            {
                int survivorSlot = BitOperations.TrailingZeroCount(occupied);
                Debug.Assert(
                    occupants.HasStoredEncoding || results[survivorSlot].Kind != ResultKind.Absent,
                    "a seeded frame's occupant rides in results; falling back here would mint a second unleased run");

                // an untouched value survivor was never copied into results — read it straight from the occupant
                NodeResult survivor = results[survivorSlot].Kind != ResultKind.Absent ? results[survivorSlot].Lease() : occupants[survivorSlot];
                changed = CollapseToChain(key, survivorSlot, survivor, touchedMask, storedChildMask, beforeHash, afterStats, out NodeResult chain);
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
                int hoistedSlot = rootKind == NodeKind.Absent ? -1 : BitOperations.TrailingZeroCount(occupied);
                NodeResult hoisted = hoistedSlot < 0 ? default
                    : results[hoistedSlot].Kind != ResultKind.Absent ? results[hoistedSlot] : occupants[hoistedSlot];
                changed = hoisted.NodeHash() != beforeHash;
                Release(results, hoistedSlot);
                return hoisted;
            }

            // Every touched child came back unchanged, so the rebuild would reproduce the stored encoding
            // byte for byte: a child that changed kind would have reported changed, leaving `occupied` and
            // `stems` — hence the shape — those of `existing`, every hash the fold would cache is the one
            // already encoded, and an unchanged child hash is an unchanged subtree, so the statistics in
            // the header hoist nothing either. That makes the rewrite below a no-op too, which its guard
            // concludes only after building the encoding it then drops. Only a frame with a stored group
            // encoding qualifies: a seeded one has no such blob to leave in place, so it always plants.
            if (changedMask == 0 && occupants.HasStoredEncoding)
            {
                Debug.Assert(
                    existing.KindAt(PbtTrieNodeGroup.RootPosition) == NodeKind.Absent,
                    "the root position holds no entry: an internal root is folded, and a lone stem sits at its boundary");
                Debug.Assert(shape.Deltas.IsZero, "an unchanged child hash is an unchanged subtree, which moves no statistic");
                changed = false;
                Release(results, handedUp: -1);

                // Nothing to persist — the existing blob stays. The root group's lone stem is read straight
                // out of it at its boundary; an internal root has no entry, so its hash — what the parent
                // already cached — is the result.
                if (rootKind == NodeKind.Stem)
                {
                    Debug.Assert(isRoot, "only the root group keeps a lone stem; elsewhere it hoists out");
                    PbtTrieNodeGroup.Slot rootSlot = existing[PbtTrieNodeGroup.BoundaryPosition(BitOperations.TrailingZeroCount(occupied))];
                    return NodeResult.StemNode(rootSlot.Stem, rootSlot.Hash);
                }
                return NodeResult.Internal(beforeHash);
            }

            // Hand the fold only the boundaries it cannot read back out of the existing encoding, in slot
            // order: the changed ones, and — when there is no existing group to read from (a run split or a
            // pushed stem seeds its occupants into an empty one) — every occupied slot. A changed slot that
            // emptied out contributes no boundary, so only the occupied ones go in.
            Span<(int Slot, Boundary Node)> changedBoundaries = stackalloc (int, Boundary)[PbtTrieNodeGroup.BoundarySlots];
            int changedCount = 0;
            uint gather = occupied & (occupants.HasStoredEncoding ? changedMask : ~0u);
            for (uint remaining = gather; remaining != 0; remaining &= remaining - 1)
            {
                // ascending, as the fold requires
                int slot = BitOperations.TrailingZeroCount(remaining);
                Debug.Assert(
                    occupants.HasStoredEncoding || results[slot].Kind != ResultKind.Absent,
                    "a seeded frame has no encoding to read a boundary back out of, so every occupied slot must ride in results");

                changedBoundaries[changedCount++] = (slot, new Boundary(results[slot].Hash, (stems >> slot & 1) != 0 ? results[slot].Stem : default));
            }

            (FoldedNode rootFold, ValueHash256 rootHash, RefCountingMemory memory) = RebuildGroup(changedBoundaries[..changedCount], existing, occupied, stems, changedMask, afterStats);

            // A stem root's stem and subtree root are read out of the fresh encoding while it is still held;
            // an internal root has no entry, its hash coming from the fold via rootHash instead.
            Stem rootStem = default;
            ValueHash256 rootSubtreeRoot = default;
            if (rootFold.IsStored)
            {
                PbtTrieNodeGroup.Slot rootSlot = PbtTrieNodeGroup.SlotAt(memory.GetSpan(), ToNodeKind(rootFold.Kind), rootFold.Offset);
                rootStem = rootSlot.Stem;
                rootSubtreeRoot = rootSlot.Hash;
            }

            // Unchanged root => the encoding is byte-identical to what is stored (an internal root whose
            // hash matches implies the same subtree, hence the same cached boundary hashes). rootHash is the
            // node hash the fold produced: a stem root's stem-node hash, or an internal root's folded hash.
            changed = rootHash != beforeHash;

            // Plant each child's own blob at its key — a run that lands here, or a group a descent
            // rebuilt. The frame that built it never wrote it; this one, its parent, does.
            for (int slot = 0; slot < PbtTrieNodeGroup.BoundarySlots; slot++)
            {
                if (results[slot].Blob is { } childBlob)
                {
                    childBlob.AcquireLease();
                    store.SetTrieNode(key.ChildGroup(slot), childBlob);
                }
            }

            Release(results, handedUp: -1);

            // This group's own encoding is handed up for the parent to plant, unless the writes left it
            // byte-identical to what is stored, when it is dropped and the existing blob stays.
            RefCountingMemory? ownBlob = changed || !occupants.HasStoredEncoding ? memory : null;
            if (ownBlob is null) ((IDisposable)memory).Dispose();
            return rootFold.IsStored
                ? NodeResult.StemNode(rootStem, rootSubtreeRoot, ownBlob)
                : NodeResult.Internal(rootHash, ownBlob);
        }

        /// <summary>
        /// Folds a group left with the single occupied child <paramref name="survivor"/> into the run that
        /// replaces it, and reports whether that leaves the frame's root node changed. The frame's own key
        /// is left untouched: its consumer decides whether to plant the run there or absorb it.
        /// </summary>
        /// <param name="stats">
        /// What the group's subtree amounts to now. One occupied slot holds all of it, so it is the
        /// surviving child's as much as the group's, and the run replacing them reaches exactly it —
        /// which is what spares the read below from having to fetch it.
        /// </param>
        private bool CollapseToChain(
            in TrieNodeKey key, int slot, NodeResult survivor, uint touchedMask, uint storedChildMask,
            in ValueHash256 beforeHash, in PbtSubtreeStats stats, out NodeResult chain)
        {
            TrieNodeKey childKey = key.ChildGroup(slot);
            bool childStored = (storedChildMask >> slot & 1) != 0;

            // The one thing the descent has not already read. An untouched child never had a frame of its
            // own, and a boundary internal does not say whether its blob is a group — leaving a run of four
            // levels — or a run this one must merge with to stay maximal. Only deletes strand one, having
            // emptied every sibling.
            if (survivor.Kind == ResultKind.Internal && (touchedMask >> slot & 1) == 0)
            {
                using RefCountingMemory? childData = store.GetTrieNode(childKey);
                if (childData is not null && PbtNodeChain.IsChain(childData.GetSpan()))
                {
                    survivor.Dispose();
                    childData.AcquireLease();
                    survivor = NodeResult.Chain(childData);
                    childStored = true;
                }
            }

            // the fold to this frame's depth happens here, before the comparison: unlike a stem's, a run's
            // node hash is its path's, so it is only the same node once it starts where beforeHash did
            chain = WrapIntoChain(key.Depth, childKey, survivor, childStored, stats);
            return chain.NodeHash() != beforeHash;
        }

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

        /// <summary>
        /// The kind of the node a boundary range folds to, given the range's occupied slots
        /// <paramref name="occupied"/> and which of the group's slots hold stems
        /// <paramref name="stems"/>: an unoccupied range is absent, a lone stem stays a stem —
        /// hoisting to its shortest unique prefix higher up — and anything else roots an internal node.
        /// </summary>
        /// <remarks>
        /// This is the fold's whole kind algebra, and it needs only the boundary results: it lets a
        /// node's shape be decided without walking below it, which is what allows the rebuild to
        /// emit nodes in encoding order.
        /// </remarks>
        private static NodeKind KindOf(uint occupied, uint stems) =>
            occupied == 0 ? NodeKind.Absent
            : BitOperations.PopCount(occupied) == 1 && (stems & occupied) != 0 ? NodeKind.Stem
            : NodeKind.Internal;

        /// <summary>
        /// Rebuilds a group straight into a fresh encoding, folding its <paramref name="changed"/> boundary
        /// values and reading every unchanged one back out of <paramref name="existing"/>.
        /// </summary>
        /// <param name="changed">
        /// The changed boundary values in ascending slot order — the only ones the fold cannot recover from
        /// <paramref name="existing"/>, since an unchanged boundary sits at a fixed position however its
        /// siblings shift.
        /// </param>
        /// <returns>
        /// The encoding, its folded root — a stem root as an offset into the encoding to read back, or — the
        /// usual case — an internal root the encoding stores no entry for, the parent caching it in its
        /// boundary slot — and that root's node hash. The caller takes the lease on the memory with it.
        /// </returns>
        /// <param name="stats">What the whole subtree amounts to, for the group's header; see <see cref="PbtTrieNodeGroup.Builder.Finish"/>.</param>
        private (FoldedNode Root, ValueHash256 RootHash, RefCountingMemory Memory) RebuildGroup(
            ReadOnlySpan<(int Slot, Boundary Node)> changed, PbtTrieNodeGroup existing, uint occupied, uint stems, uint changedMask,
            in PbtSubtreeStats stats)
        {
            (uint presence, uint stemMask) = PredictShape(existing, occupied, stems, changedMask, PbtTrieNodeGroup.RootPosition, 0, PbtTrieNodeGroup.BoundarySlots, writeFormat);
            RefCountingMemory memory = memoryProvider.Rent(PbtTrieNodeGroup.EncodedLength(presence, stemMask));

            GroupRebuild rebuild = new(changed, existing, occupied, stems, changedMask, memory.GetSpan(), writeFormat);
            // an internal group root is folded to a by-value hash and never stored, the parent caching it
            // in its boundary slot; only a stem root is written, which the caller reads back
            FoldedNode root = rebuild.Fold(PbtTrieNodeGroup.RootPosition, 0, PbtTrieNodeGroup.BoundarySlots, out ValueHash256 rootHash);
            int length = rebuild.Finish(stats);
            Debug.Assert(length == memory.GetSpan().Length, "the predicted shape must size the fold's encoding exactly");

            return (root, rootHash, memory);
        }

        /// <summary>
        /// The presence and stem bitmaps <see cref="GroupRebuild.Fold"/> will produce over
        /// <c>[firstSlot, firstSlot + width)</c>, given the boundary results described by
        /// <paramref name="occupied"/> and <paramref name="stems"/>.
        /// </summary>
        /// <remarks>
        /// Mirrors the fold's walk on the masks alone — no hashing, no reads — so the group's encoding
        /// can be sized before its buffer is rented, as <see cref="StemLeafBlob.RebuildState"/> sizes a
        /// leaf blob from its stored-node counts. This is well-defined because a node's kind follows from
        /// <see cref="KindOf"/> over its range, which needs only the boundary masks. It must prune where
        /// the fold prunes, which is what sharing <see cref="IsCleanRange"/> keeps it doing: a range
        /// copied verbatim keeps the shape it already had.
        /// </remarks>
        private static (uint Presence, uint Stems) PredictShape(
            PbtTrieNodeGroup existing, uint occupied, uint stems, uint changed, int position, int firstSlot, int width,
            PbtGroupFormat format)
        {
            uint range = ((1u << width) - 1) << firstSlot;
            switch (KindOf(occupied & range, stems))
            {
                case NodeKind.Absent:
                    return default;
                case NodeKind.Stem:
                    // a lone stem lands at its boundary position; the shape walk recurses down to it just
                    // as the fold does, so it is stored there and nothing above it is emitted
                    if (width == 1) return (1u << position, 1u << position);
                    int stemHalf = width / 2;
                    return (occupied & range & (((1u << stemHalf) - 1) << firstSlot)) != 0
                        ? PredictShape(existing, occupied, stems, changed, position - width, firstSlot, stemHalf, format)
                        : PredictShape(existing, occupied, stems, changed, position - 1, firstSlot + stemHalf, stemHalf, format);
            }

            if (IsCleanRange(existing, changed, range, position, width, format))
            {
                existing.SubtreeBitmaps(position, width, out uint cleanPresence, out uint cleanStems);
                return (cleanPresence, cleanStems);
            }

            // an internal node the format skips — and the group root whatever the format — is folded,
            // not stored, so it takes no room
            uint self = PbtTrieNodeGroup.StoresInternalAtWidth(format, width) && width != PbtTrieNodeGroup.BoundarySlots ? 1u << position : 0;
            if (width == 1) return (self, 0);

            int half = width / 2;
            (uint leftPresence, uint leftStems) = PredictShape(existing, occupied, stems, changed, position - width, firstSlot, half, format);
            (uint rightPresence, uint rightStems) = PredictShape(existing, occupied, stems, changed, position - 1, firstSlot + half, half, format);
            return (leftPresence | rightPresence | self, leftStems | rightStems);
        }

        /// <summary>A reference to a node in the encoding holding it: its kind and its entry offset.</summary>
        private readonly record struct NodeRef(ResultKind Kind, int Offset);

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
        private readonly struct Occupant(NodeRef node, RefCountingMemory? memory) : IDisposable
        {
            private readonly NodeRef _node = node;
            private readonly RefCountingMemory? _memory = memory;

            /// <summary>
            /// Promotes an occupant to a result. A run carries its blob up for an ancestor to plant; an
            /// internal or stem, already persisted where it sits, becomes a pure value with no blob of its own.
            /// </summary>
            public static implicit operator NodeResult(Occupant occupant) => occupant.Kind switch
            {
                ResultKind.Chain => NodeResult.Chain(occupant._memory!),
                ResultKind.Stem => NodeResult.StemNode(occupant.Stem, occupant.Hash),
                ResultKind.Internal => NodeResult.Internal(occupant.Hash),
                _ => default,
            };

            public ResultKind Kind => _node.Kind;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Stem"/>
            public Stem Stem => Slot.Stem;

            /// <summary>A run's encoding, borrowed from the memory this node leases.</summary>
            public ReadOnlySpan<byte> ChainData
            {
                get
                {
                    Debug.Assert(_node.Kind == ResultKind.Chain && _node.Offset == 0, "only a run is an encoding all of its own");
                    return _memory!.GetSpan();
                }
            }

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Hash"/>
            public ValueHash256 Hash => _node.Kind == ResultKind.Chain ? PbtNodeChain.NodeHashOf(ChainData) : Slot.Hash;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.NodeHash"/>
            /// <remarks>A run's cached node hash: what it contributes to its parent, as an internal node's is.</remarks>
            public ValueHash256 NodeHash() => _node.Kind == ResultKind.Chain ? PbtNodeChain.NodeHashOf(ChainData) : Slot.NodeHash();

            private PbtTrieNodeGroup.Slot Slot =>
                _memory is null ? default : PbtTrieNodeGroup.SlotAt(_memory.GetSpan(), ToNodeKind(_node.Kind), _node.Offset);

            public void Dispose() => ((IDisposable?)_memory)?.Dispose();
        }

        /// <summary>
        /// The result of applying a batch to a slot: the node now occupying it, handed up for a parent to
        /// place. Usually backed by an encoding, but the folded internal group root has no entry, so it is
        /// carried by value instead — see <see cref="Unstored"/>.
        /// </summary>
        /// <remarks>
        /// The descent's output, distinct from its input <see cref="Occupant"/>. Most results are a stored
        /// node — an existing group's node, a stem built at its shortest unique prefix in memory of its own,
        /// or a run whose <see cref="PbtNodeChain"/> encoding no key holds until an ancestor plants it — and
        /// the lease keeps such a node safe to hand up: it outlives the buffer's other owners, whether a
        /// <c>using</c> in the frame it leaves or an <see cref="IPbtStore"/> write taking the encoding with
        /// it. The one exception is a group's internal root, which no encoding stores (the parent caches it
        /// in its boundary slot), so it carries its hash by value, as <see cref="FoldedNode.Unstored"/> does
        /// for a skipped odd level one frame down. An absent result holds nothing, so <c>default</c> is one.
        /// </remarks>
        private readonly struct NodeResult : IDisposable
        {
            private readonly ResultKind _kind;
            private readonly ValueHash256 _hash;
            private readonly Stem _stem;
            private readonly RefCountingMemory? _blob;

            private NodeResult(ResultKind kind, in ValueHash256 hash, in Stem stem, RefCountingMemory? blob)
            {
                _kind = kind;
                _hash = hash;
                _stem = stem;
                _blob = blob;
            }

            /// <summary>An internal node folding to <paramref name="hash"/>, with a rebuilt group <paramref name="blob"/> to persist at its key when one changed.</summary>
            public static NodeResult Internal(in ValueHash256 hash, RefCountingMemory? blob = null) => new(ResultKind.Internal, hash, default, blob);

            /// <summary>A stem node; <paramref name="blob"/> is the root group's encoding when its lone root stem sits in one, else <c>null</c> (a hoisting stem has no blob of its own).</summary>
            public static NodeResult StemNode(in Stem stem, in ValueHash256 subtreeRoot, RefCountingMemory? blob = null) => new(ResultKind.Stem, subtreeRoot, stem, blob);

            /// <summary>A run, whose <paramref name="blob"/> is its <see cref="PbtNodeChain"/> encoding for an ancestor to plant or absorb.</summary>
            public static NodeResult Chain(RefCountingMemory blob) => new(ResultKind.Chain, PbtNodeChain.NodeHashOf(blob.GetSpan()), default, blob);

            public ResultKind Kind => _kind;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Hash"/>
            public ValueHash256 Hash => _hash;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Stem"/>
            public Stem Stem => _stem;

            /// <summary>
            /// The encoding to persist at this node's key — a rebuilt group or a run — or <c>null</c> when
            /// there is nothing new to write there (an unchanged group, a hoisting stem, an absent subtree).
            /// </summary>
            public RefCountingMemory? Blob => _blob;

            /// <summary>A run's encoding, borrowed from the memory this result leases.</summary>
            public ReadOnlySpan<byte> ChainData
            {
                get
                {
                    Debug.Assert(_kind == ResultKind.Chain, "only a run is an encoding all of its own");
                    return _blob!.GetSpan();
                }
            }

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.NodeHash"/>
            public ValueHash256 NodeHash() => _kind == ResultKind.Stem ? StemLeafBlob.ComputeStemNodeHash(_stem, _hash) : _hash;

            /// <summary>Takes a second lease on the blob, for another owner to release in its own time.</summary>
            public NodeResult Lease()
            {
                _blob?.AcquireLease();
                return this;
            }

            public void Dispose() => ((IDisposable?)_blob)?.Dispose();
        }

        /// <summary>
        /// A boundary node's value as the fold consumes it: the hash it contributes, and its stem when it
        /// is a stem node. Projected from the descent's <see cref="NodeResult"/>s once, so the fold reads
        /// plain values rather than reaching back through their leases.
        /// </summary>
        private readonly record struct Boundary(ValueHash256 Hash, Stem Stem);

        /// <summary>Folds one stem's writes (<paramref name="changes"/>) into its leaf blob, persists it, and reports whether the stem is now empty.</summary>
        private bool ComputeBlob(in Stem stem, IPbtStemChanges changes, out ValueHash256 subtreeRoot)
        {
            using RefCountingMemory? prior = store.GetLeafBlob(stem);
            using StemLeafBlob.RebuildState newBlob = StemLeafBlob.Apply(prior is null ? default : prior.GetSpan(), changes, memoryProvider);
            subtreeRoot = newBlob.SubtreeRoot;
            bool isEmpty = newBlob.IsEmpty;
            store.SetLeafBlob(stem, newBlob.Take());
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
        private static void AssertUntouchedMerged<TOccupants>(in TOccupants occupants, uint occupied, uint stems, uint touchedMask)
            where TOccupants : IOccupants, allows ref struct
        {
            for (uint untouched = ~touchedMask & ((1u << PbtTrieNodeGroup.BoundarySlots) - 1); untouched != 0; untouched &= untouched - 1)
            {
                int slot = BitOperations.TrailingZeroCount(untouched);
                ResultKind kind = occupants[slot].Kind;

                // a constant message: Debug.Assert evaluates its argument on every call, and this one runs
                // per untouched slot of every frame
                Debug.Assert(
                    (occupied >> slot & 1) != 0 == (kind != ResultKind.Absent) && (stems >> slot & 1) != 0 == (kind == ResultKind.Stem),
                    "an untouched slot disagrees with its occupant — the caller skipped GroupShape.MergeUntouched");
            }
        }

        /// <summary>
        /// The touched mask a level's bounds imply, for the assertion that whoever bucketed the range
        /// cached the same thing.
        /// </summary>
        private static uint DeriveTouchedMask(ReadOnlySpan<int> bounds)
        {
            uint touched = 0;
            for (int slot = 0; slot < PbtTrieNodeGroup.BoundarySlots; slot++)
            {
                if (bounds[slot] != bounds[slot + 1]) touched |= 1u << slot;
            }

            return touched;
        }

        /// <summary>
        /// The precalculated bucket level for the child group under boundary slot <paramref name="slot"/>,
        /// or empty when <paramref name="buckets"/> is the last level the producer bucketed.
        /// </summary>
        /// <remarks>
        /// The levels are laid out coarsest-last, each one <see cref="PbtWriteBatch.LevelStride"/> wide per
        /// group it describes, so the finer levels of a slot's subtree are the slice at its own index — and
        /// what remains after this group's own level divides evenly among the sixteen slots.
        /// </remarks>
        private static ReadOnlySpan<int> ChildBuckets(ReadOnlySpan<int> buckets, int slot)
        {
            int childLength = buckets.Length <= PbtWriteBatch.LevelStride
                ? 0
                : (buckets.Length - PbtWriteBatch.LevelStride) / PbtTrieNodeGroup.BoundarySlots;
            return childLength == 0 ? default : buckets.Slice(slot * childLength, childLength);
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
        private static void Partition(Span<PbtWriteBatch.StemEntry> entries, int depth, Span<int> level)
        {
            Span<int> bounds = level[..PbtWriteBatch.BoundsLength];
            // Only the levels the producer left unbucketed reach here, which for a batch drained from
            // PbtWriteBatchBuilder — its shard key handing over the depth-0 and depth-4 bounds — is
            // everything below depth 8, by which point the batch has fragmented sixteen ways twice and a
            // range is almost always one or a few entries. The count-and-permute below costs its
            // sixteen-wide passes whether it buckets one entry or a hundred, so the tiny ranges that
            // dominate take a sort network instead.
            if (entries.Length <= TinyRange)
            {
                SortTiny(entries, depth, level);
                return;
            }

            Span<int> counts = stackalloc int[PbtTrieNodeGroup.BoundarySlots];
            counts.Clear();
            for (int i = 0; i < entries.Length; i++) counts[NibbleOf(entries[i].Stem, depth)]++;

            int total = 0;
            uint touched = 0;
            for (int bucket = 0; bucket < PbtTrieNodeGroup.BoundarySlots; bucket++)
            {
                bounds[bucket] = total;
                if (counts[bucket] != 0) touched |= 1u << bucket;
                total += counts[bucket];
            }
            bounds[PbtTrieNodeGroup.BoundarySlots] = total;
            level[PbtWriteBatch.TouchedMaskIndex] = (int)touched;
            Debug.Assert(total == entries.Length);

            // In-place American-flag permutation: each swap places one entry into its final bucket.
            Span<int> heads = stackalloc int[PbtTrieNodeGroup.BoundarySlots];
            bounds[..PbtTrieNodeGroup.BoundarySlots].CopyTo(heads);
            for (int bucket = 0; bucket < PbtTrieNodeGroup.BoundarySlots; bucket++)
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
        private static void SortTiny(Span<PbtWriteBatch.StemEntry> entries, int depth, Span<int> level)
        {
            Debug.Assert(!entries.IsEmpty && entries.Length <= TinyRange);

            Span<int> bounds = level[..PbtWriteBatch.BoundsLength];

            Span<int> nibbleBuffer = stackalloc int[TinyRange];
            Span<int> nibbles = nibbleBuffer[..entries.Length];
            uint touched = 0;
            for (int i = 0; i < nibbles.Length; i++)
            {
                nibbles[i] = NibbleOf(entries[i].Stem, depth);
                touched |= 1u << nibbles[i];
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
        }

        /// <summary>Orders <paramref name="entries"/> <paramref name="i"/> and <paramref name="j"/> by their nibble.</summary>
        private static void CompareAndSwap(Span<PbtWriteBatch.StemEntry> entries, Span<int> nibbles, int i, int j)
        {
            if (nibbles[i] <= nibbles[j]) return;
            (entries[i], entries[j]) = (entries[j], entries[i]);
            (nibbles[i], nibbles[j]) = (nibbles[j], nibbles[i]);
        }

        private static int NibbleOf(in Stem stem, int depth) => NibbleAt(stem.Bytes[depth >> 3], depth);

        private static int NibbleAt(byte value, int depth) => (depth & 4) == 0 ? value >> 4 : value & 0xF;
    }
}
