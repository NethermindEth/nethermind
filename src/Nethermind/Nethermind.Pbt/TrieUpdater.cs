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

public static class PbtStoreExtensions
{
    /// <summary>
    /// Copies a value handed to an <see cref="IPbtStore"/> write into a fresh array and releases the
    /// lease that came with it, for a store that keeps its values as arrays; a removal
    /// (<c>null</c>) copies to <c>null</c>.
    /// </summary>
    public static byte[]? ToArrayAndRelease(this RefCountingMemory? memory)
    {
        using (memory) return memory?.GetSpan().ToArray();
    }
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
public static class TrieUpdater
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
        changes.Count == 0 ? currentRoot : new Updater(store, memoryProvider, writeFormat).Run(changes);

    private sealed class Updater(IPbtStore store, IRefCountingMemoryProvider memoryProvider, PbtGroupFormat writeFormat)
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

        /// <summary>What the store holds at a frame's own key, which its content alone no longer tells it.</summary>
        /// <remarks>
        /// A frame reached through <see cref="ApplyChainSplit"/> has a blob but reads as the empty
        /// group, since a chain is no group's encoding — so "a blob is stored here" and "there are
        /// cached hashes to reuse" have to be asked separately.
        /// </remarks>
        private enum StoredBlob : byte { None, Group, Chain }

        public ValueHash256 Run(PbtWriteBatch changes)
        {
            // No global sort: each group radix-partitions its own range in place during the descent,
            // bar the levels the producer already bucketed for it.
            using RefCountingMemory? rootData = store.GetTrieNode(TrieNodeKey.Root);
            using NodeResult root = ApplyStored(TrieNodeKey.Root, changes.Entries, rootData, changes.Buckets, out _, out _);
            return root.NodeHash();
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to whatever is stored at <paramref name="key"/>, which the
        /// two blob formats' disjoint leading bytes tell apart.
        /// </summary>
        /// <param name="existingData"><inheritdoc cref="ApplyGroup" path="/param[@name='existingData']"/></param>
        /// <param name="precalculatedBuckets"><inheritdoc cref="ApplyToOccupants" path="/param[@name='precalculatedBuckets']"/></param>
        /// <param name="changed"><inheritdoc cref="ApplyToOccupants" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="ApplyToOccupants" path="/param[@name='delta']"/></param>
        private NodeResult ApplyStored(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, RefCountingMemory? existingData,
            ReadOnlySpan<int> precalculatedBuckets, out bool changed, out PbtSubtreeStats delta) =>
            existingData is not null && PbtNodeChain.IsChain(existingData.GetSpan())
                ? ApplyChain(key, entries, existingData.GetSpan(), StoredBlob.Chain, precalculatedBuckets, out changed, out delta)
                : ApplyGroup(key, entries, existingData, precalculatedBuckets, out changed, out delta);

        /// <summary>The kind a group's encoding gives a node, as a boundary result.</summary>
        private static ResultKind ToResultKind(NodeKind kind) => (ResultKind)kind;

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
        /// <paramref name="key"/> and returns the node now occupying the group's root position for the
        /// parent's boundary slot. A stem result is not written here: it hoists into the parent,
        /// cascading across group boundaries (except at the root group, whose root may hold a stem).
        /// The caller disposes the result.
        /// </summary>
        /// <param name="existingData">
        /// The group's stored encoding, which the caller owns and this keeps no lease on beyond the
        /// call: the occupants read out of it — and the root, when the writes leave the group as it
        /// was — take their own.
        /// </param>
        /// <param name="precalculatedBuckets"><inheritdoc cref="ApplyToOccupants" path="/param[@name='precalculatedBuckets']"/></param>
        /// <param name="changed"><inheritdoc cref="ApplyToOccupants" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="ApplyToOccupants" path="/param[@name='delta']"/></param>
        private NodeResult ApplyGroup(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, RefCountingMemory? existingData,
            ReadOnlySpan<int> precalculatedBuckets, out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty && depth % PbtTrieNodeGroup.LevelsPerGroup == 0);
            Debug.Assert(depth <= PbtTrieNodeGroup.MaxGroupDepth);

            PbtTrieNodeGroup existing = existingData is null ? default : PbtTrieNodeGroup.Decode(existingData.GetSpan());

            // Route the existing occupants to their boundary slots for the rebuild: a stem anywhere in
            // the group goes to the slot its path passes through at `depth` (the rebuild recomputes its
            // position), and a boundary internal — the cached pointer to a child group — to its own slot.
            // Inner internals are recomputed or copied by the rebuild and are skipped. An empty group
            // reads as all-absent, seeding nothing. Each occupant stays where it already is, in this
            // group's own encoding, under a lease of its own. Iterating positions and pushing each to a
            // slot (rather than each slot pulling its occupant leaf-then-parent) visits every stem once:
            // a stem at an inner position routes to just the one slot its nibble selects, which a per-slot
            // walk-up would rediscover from every slot the stem covers.
            RefList16<NodeResult> occupantBuffer = new(PbtTrieNodeGroup.BoundarySlots);
            Span<NodeResult> occupants = occupantBuffer.AsSpan();
            for (int position = 0; position < PbtTrieNodeGroup.PositionCount; position++)
            {
                NodeKind kind = existing.KindAt(position);
                int bucket;
                if (kind == NodeKind.Stem)
                {
                    bucket = NibbleOf(existing[position].Stem, depth);
                    Debug.Assert(occupants[bucket].Kind == ResultKind.Absent, "two occupants routed to one boundary slot");
                }
                else if (kind == NodeKind.Internal && PbtTrieNodeGroup.IsBoundaryPosition(position))
                {
                    bucket = PbtTrieNodeGroup.BoundarySlot(position);
                }
                else
                {
                    continue;
                }

                existingData!.AcquireLease();
                occupants[bucket] = new NodeResult(new NodeRef(ToResultKind(kind), existing.EntryOffset(position)), existingData);
            }

            NodeResult root = ApplyToOccupants(
                key, entries, existing, existingData, existingData is null ? StoredBlob.None : StoredBlob.Group,
                occupants, existing[PbtTrieNodeGroup.RootPosition].NodeHash(), existing.Stats, precalculatedBuckets,
                out changed, out delta);
            Release(occupants, handedUp: -1);
            return root;
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to a subtree with no stored group at <paramref name="key"/>:
        /// either empty, or holding a single stem <paramref name="pushed"/> down from the parent's
        /// boundary slot. A range holding one uncontended stem has reached the shortest prefix no other
        /// stem shares, so that stem's node is built here, out of its leaf blob, without descending;
        /// otherwise the pushed stem and the writes are routed on down together.
        /// </summary>
        /// <param name="precalculatedBuckets"><inheritdoc cref="ApplyToOccupants" path="/param[@name='precalculatedBuckets']"/></param>
        /// <param name="changed"><inheritdoc cref="ApplyToOccupants" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="ApplyToOccupants" path="/param[@name='delta']"/></param>
        private NodeResult ApplyPushed(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in NodeResult pushed,
            ReadOnlySpan<int> precalculatedBuckets, out bool changed, out PbtSubtreeStats delta)
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
                NodeResult stemNode = isEmpty ? default : NewStemNode(stem, subtreeRoot);
                changed = stemNode.NodeHash() != pushed.NodeHash();

                // The one place a stem is born or dies, so the one place a delta starts: this range
                // held a stem or it did not, and it holds one now or it does not.
                delta = (isEmpty ? default : PbtSubtreeStats.OneStem) - beforeStats;
                return stemNode;
            }

            // Every entry in the range shares bits [0, depth) with `stem`, so at the stem's full depth
            // they are all this same stem, and the range not having collapsed above means it holds more
            // than one entry for it: the producer failed to merge its writes to the stem, which
            // PbtWriteBatch requires of it. Nothing is left to partition on, so this must not descend.
            if (depth == Stem.LengthInBits) throw new InvalidOperationException($"Stem {stem} written more than once in a single batch");

            Debug.Assert(depth <= PbtTrieNodeGroup.MaxGroupDepth);

            RefList16<NodeResult> occupantBuffer = new(PbtTrieNodeGroup.BoundarySlots);
            Span<NodeResult> occupants = occupantBuffer.AsSpan();
            if (pushed.Kind == ResultKind.Stem) occupants[NibbleOf(pushed.Stem, depth)] = pushed.Lease();

            NodeResult root = ApplyToOccupants(
                key, entries, default, null, StoredBlob.None, occupants, pushed.NodeHash(), beforeStats, precalculatedBuckets,
                out changed, out delta);
            Release(occupants, handedUp: -1);
            return root;
        }

        /// <summary>
        /// Builds the stem node for <paramref name="stem"/> over the root of its freshly merkelized
        /// leaf blob, in memory of its own: no group encodes this node yet — an enclosing rebuild
        /// copies it into one — so nothing else holds the bytes to point at.
        /// </summary>
        private NodeResult NewStemNode(in Stem stem, in ValueHash256 subtreeRoot)
        {
            RefCountingMemory memory = memoryProvider.Rent(PbtTrieNodeGroup.Slot.StemLength);
            PbtTrieNodeGroup.WriteStem(memory.GetSpan(), stem, subtreeRoot);
            return new NodeResult(new NodeRef(ResultKind.Stem, 0), memory);
        }

        /// <summary>
        /// Builds the run of single-child levels from <paramref name="startDepth"/> down to the group at
        /// <paramref name="targetDepth"/>, whose subtree amounts to <paramref name="stats"/>, in memory of
        /// its own: a run is part of no group's encoding, and no blob holds its bytes until an ancestor
        /// plants them.
        /// </summary>
        private NodeResult NewChainNode(
            int startDepth, int targetDepth, in Stem targetPath, in ValueHash256 targetHash, in PbtSubtreeStats stats)
        {
            RefCountingMemory memory = memoryProvider.Rent(PbtNodeChain.EncodedLength);
            PbtNodeChain.Write(memory.GetSpan(), startDepth, targetDepth, targetPath, targetHash, stats);
            return new NodeResult(new NodeRef(ResultKind.Chain, 0), memory);
        }

        /// <summary>
        /// Builds the boundary internal caching <paramref name="hash"/> in memory of its own, for a
        /// pointer to a stored group that no group's encoding holds yet.
        /// </summary>
        private NodeResult NewInternalNode(in ValueHash256 hash)
        {
            RefCountingMemory memory = memoryProvider.Rent(PbtTrieNodeGroup.Slot.InternalLength);
            hash.Bytes.CopyTo(memory.GetSpan());
            return new NodeResult(new NodeRef(ResultKind.Internal, 0), memory);
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to the run of single-child levels at <paramref name="key"/>,
        /// whose content is <paramref name="chainData"/> — a stored <see cref="PbtNodeChain"/> blob, or the
        /// rest of one an enclosing frame is descending.
        /// </summary>
        /// <remarks>
        /// The run holds no node between its start and its target, so nothing here walks it level by
        /// level: the entries' shallowest departure from its path says where the trie next branches, and
        /// the descent resumes at the group holding that bit — or at the target itself, when they all pass
        /// through.
        /// </remarks>
        /// <param name="precalculatedBuckets"><inheritdoc cref="ApplyToOccupants" path="/param[@name='precalculatedBuckets']"/></param>
        /// <param name="changed"><inheritdoc cref="ApplyToOccupants" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="ApplyToOccupants" path="/param[@name='delta']"/></param>
        private NodeResult ApplyChain(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, ReadOnlySpan<byte> chainData, StoredBlob stored,
            ReadOnlySpan<int> precalculatedBuckets, out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty);

            PbtNodeChain chain = PbtNodeChain.Decode(chainData, depth);
            int target = chain.TargetDepth;
            Stem targetPath = chain.TargetPath;

            // The shallowest bit at which a write leaves the run. FirstDifferingBit reports -1 for a stem
            // that never parts from the path, which as an unsigned compare reads as past everything —
            // which is what never parting means: the write descends the whole run.
            int split = target;
            for (int i = 0; i < entries.Length; i++)
            {
                int diff = entries[i].Stem.FirstDifferingBit(targetPath, depth);
                if ((uint)diff < (uint)split) split = diff;
            }

            Debug.Assert(split >= depth, "the entries share the run's path down to this frame");

            // Nothing parts from the run inside it, so the writes land in its target group. Decided before
            // the branch depth below, which for split == target would name the target's own depth — a run
            // of no levels.
            if (split == target) return ApplyChainThrough(key, entries, chain, chain.TargetKey, stored, out changed, out delta);

            int branchDepth = split & ~(PbtTrieNodeGroup.LevelsPerGroup - 1);

            // The branch falls inside this frame's own four levels, so this frame is a group after all —
            // and it partitions this very range at this very depth, which is what the table describes.
            if (branchDepth == depth) return ApplyChainSplit(key, entries, chain, stored, precalculatedBuckets, out changed, out delta);

            // It falls deeper: the run keeps its shallow levels and resumes at the group holding the branch.
            Debug.Assert(branchDepth > depth && branchDepth < target);
            return ApplyChainThrough(key, entries, chain, TrieNodeKey.For(branchDepth, targetPath), stored, out changed, out delta);
        }

        /// <summary>
        /// Descends <paramref name="chain"/> straight to <paramref name="innerKey"/> — its target, or the
        /// group where a write parts from it — and folds what comes back into a run starting at
        /// <paramref name="key"/> again. Every level in between is skipped: the run holds no node to visit.
        /// </summary>
        /// <remarks>
        /// The frame this lands on partitions for itself: a precalculated bucket level describes one range
        /// at one depth, and skipping levels is exactly what leaves this frame's table describing neither.
        /// A run only ever reaches past the topmost levels a producer buckets, so nothing is given up.
        /// </remarks>
        private NodeResult ApplyChainThrough(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in PbtNodeChain chain, in TrieNodeKey innerKey, StoredBlob stored,
            out bool changed, out PbtSubtreeStats delta)
        {
            Debug.Assert(innerKey.Depth > key.Depth, "a run descends, so its inner frame is past every level a table above it describes");

            NodeResult inner;
            bool innerBlobStored;
            if (innerKey.Depth == chain.TargetDepth)
            {
                // the target group is the one blob a run points at (invariant 2)
                using RefCountingMemory? targetData = store.GetTrieNode(innerKey);
                innerBlobStored = targetData is not null;
                inner = ApplyStored(innerKey, entries, targetData, default, out _, out delta);
            }
            else
            {
                // nothing is stored between a run's start and its target (invariant 3), so the frame there
                // takes its content from the rest of this run rather than from the store
                using NodeResult remainder = NewChainNode(innerKey.Depth, chain.TargetDepth, chain.TargetPath, chain.TargetHash, chain.Stats);
                innerBlobStored = false;
                inner = ApplyChain(innerKey, entries, remainder.ChainData, StoredBlob.None, default, out _, out delta);
            }

            // A run holds no stem of its own, so what its inner frame reaches is the whole of its subtree.
            NodeResult result = WrapIntoChain(key.Depth, innerKey, inner, innerBlobStored, chain.Stats + delta);
            changed = result.NodeHash() != chain.NodeHash;

            // A run that survives is a run again, and its consumer decides which key keeps it. One that
            // dissolved leaves this key empty, and only this frame knows a blob was here.
            if (result.Kind != ResultKind.Chain && stored != StoredBlob.None) store.SetTrieNode(key, null);
            return result;
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to a run whose writes branch inside its own top four levels,
        /// making the frame a group: the rest of the run becomes its one seeded occupant, and the ordinary
        /// descent takes over.
        /// </summary>
        /// <param name="precalculatedBuckets"><inheritdoc cref="ApplyToOccupants" path="/param[@name='precalculatedBuckets']"/></param>
        /// <param name="changed"><inheritdoc cref="ApplyToOccupants" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="ApplyToOccupants" path="/param[@name='delta']"/></param>
        private NodeResult ApplyChainSplit(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in PbtNodeChain chain, StoredBlob stored,
            ReadOnlySpan<int> precalculatedBuckets, out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            int childDepth = depth + PbtTrieNodeGroup.LevelsPerGroup;
            Stem targetPath = chain.TargetPath;
            ValueHash256 targetHash = chain.TargetHash;

            RefList16<NodeResult> occupantBuffer = new(PbtTrieNodeGroup.BoundarySlots);
            Span<NodeResult> occupants = occupantBuffer.AsSpan();

            // The run below this frame, at the one slot its path passes through: the target group itself
            // when it is the direct child, otherwise what is left of the run. Neither is stored under that
            // key — a boundary internal points at the target, and the remainder has never been a blob.
            occupants[NibbleOf(targetPath, depth)] = childDepth == chain.TargetDepth
                ? NewInternalNode(targetHash)
                : NewChainNode(childDepth, chain.TargetDepth, targetPath, targetHash, chain.Stats);

            // The run reached one subtree and nothing else, so it is the whole of what is here.
            NodeResult root = ApplyToOccupants(
                key, entries, default, null, stored, occupants, chain.NodeHash, chain.Stats, precalculatedBuckets,
                out changed, out delta);
            Release(occupants, handedUp: -1);
            return root;
        }

        /// <summary>
        /// Folds <paramref name="inner"/> — the node now at <paramref name="innerKey"/> — back into a run
        /// starting at <paramref name="startDepth"/>, taking <paramref name="inner"/>'s lease.
        /// </summary>
        /// <remarks>
        /// An internal node roots a run reaching down to it. A run of its own extends this one instead,
        /// which is what keeps runs maximal (invariant 2) and what leaves <paramref name="innerKey"/> a key
        /// nothing may hold any more — removed here when <paramref name="innerBlobStored"/> says a blob was
        /// read there. A stem or an empty subtree dissolves the run: a stem's node hash does not depend on
        /// where it sits, so it hoists on up untouched.
        /// </remarks>
        /// <param name="stats">
        /// What <paramref name="inner"/>'s subtree amounts to, which the run it becomes reaches and
        /// nothing else.
        /// </param>
        private NodeResult WrapIntoChain(
            int startDepth, in TrieNodeKey innerKey, NodeResult inner, bool innerBlobStored, in PbtSubtreeStats stats)
        {
            switch (inner.Kind)
            {
                case ResultKind.Absent:
                case ResultKind.Stem:
                    return inner;
                case ResultKind.Internal:
                    using (inner) return NewChainNode(startDepth, innerKey.Depth, innerKey.Path, inner.Hash, stats);
                default:
                    PbtNodeChain absorbed = PbtNodeChain.Decode(inner.ChainData, innerKey.Depth);
                    int targetDepth = absorbed.TargetDepth;
                    Stem targetPath = absorbed.TargetPath;
                    ValueHash256 targetHash = absorbed.TargetHash;
                    Debug.Assert(absorbed.Stats == stats, "an absorbed run reaches the same subtree the run absorbing it does");
                    if (innerBlobStored) store.SetTrieNode(innerKey, null);
                    using (inner) return NewChainNode(startDepth, targetDepth, targetPath, targetHash, stats);
            }
        }

        /// <summary>
        /// The shared descent of <see cref="ApplyGroup"/>, <see cref="ApplyPushed"/> and
        /// <see cref="ApplyChainSplit"/>: with the group's boundary <paramref name="occupants"/> already
        /// seeded, partitions <paramref name="entries"/> to their slots, applies each touched slot's bucket
        /// to its child, then settles the group — removing it (when it folds to nothing or to a lone
        /// hoisting stem), collapsing it to a run (when one child is left), or rebuilding and writing it.
        /// Returns the node now occupying the group's root position.
        /// </summary>
        /// <param name="existing">
        /// The group's stored content, or the empty group when there is none to read hashes back from — a
        /// pushed-stem subtree, a tree with no stored root group, or a run, which is no group's encoding.
        /// Used only to reuse cached hashes in the rebuild; <paramref name="stored"/> says what is on disk.
        /// </param>
        /// <param name="existingData">
        /// The memory <paramref name="existing"/> was decoded from, non-<c>null</c> exactly when
        /// <paramref name="stored"/> is <see cref="StoredBlob.Group"/>, for the root to be handed back out
        /// of when the writes leave the group as it was.
        /// </param>
        /// <param name="beforeHash">The hash the root position contributed before, against which <paramref name="changed"/> is decided.</param>
        /// <param name="beforeStats">
        /// What this frame's subtree amounted to before the writes, which <paramref name="delta"/> is
        /// measured from and which the rebuilt group's own statistics are hoisted onto.
        /// </param>
        /// <param name="precalculatedBuckets">
        /// The bucket table levels the producer already partitioned <paramref name="entries"/> into, this
        /// group's own being the last of them (<see cref="PbtWriteBatch.BucketTableLength"/>); empty once
        /// the descent runs past them, from where each group partitions its range itself.
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
        private NodeResult ApplyToOccupants(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, PbtTrieNodeGroup existing,
            RefCountingMemory? existingData, StoredBlob stored,
            Span<NodeResult> occupants, in ValueHash256 beforeHash, in PbtSubtreeStats beforeStats,
            ReadOnlySpan<int> precalculatedBuckets, out bool changed, out PbtSubtreeStats delta)
        {
            Debug.Assert((stored == StoredBlob.Group) == (existingData is not null), "a stored group is one this frame decoded");

            int depth = key.Depth;

            // A precalculated level is already a bounds array over this very range, so it is read where it
            // lies; only a range the producer left unbucketed is partitioned here.
            Span<int> computed = stackalloc int[PbtWriteBatch.LevelStride];
            scoped ReadOnlySpan<int> bounds;
            if (precalculatedBuckets.IsEmpty)
            {
                Partition(entries, depth, computed);
                bounds = computed;
            }
            else
            {
                bounds = precalculatedBuckets[^PbtWriteBatch.LevelStride..];
            }

            // The boundary results drive both the rebuild and its shape: `occupied` and `stems` fix
            // every node's kind (see GroupRebuild), and `changedMask` folds to a range's
            // subtree-changed flag by masking. `chainMask` marks the results that are runs, which no key
            // holds yet, and `touchedMask` / `storedChildMask` record what the descent learned about each
            // child's blob, for the collapse below. Each result leases the encoding its node lives in, so
            // every one of them is released before this frame returns, bar the node handed up.
            RefList16<NodeResult> resultBuffer = new(PbtTrieNodeGroup.BoundarySlots);
            Span<NodeResult> results = resultBuffer.AsSpan();
            uint occupied = 0;
            uint stems = 0;
            uint changedMask = 0;
            uint chainMask = 0;
            uint touchedMask = 0;
            uint storedChildMask = 0;
            PbtSubtreeStats childDeltas = default;
            for (int slot = 0; slot < PbtTrieNodeGroup.BoundarySlots; slot++)
            {
                Span<PbtWriteBatch.StemEntry> bucket = entries[bounds[slot]..bounds[slot + 1]];
                if (bucket.IsEmpty)
                {
                    // untouched: reuse the cached boundary hash / stem, never loading the child group —
                    // and its subtree is as it was, so it hoists nothing
                    results[slot] = occupants[slot].Lease();
                }
                else
                {
                    touchedMask |= 1u << slot;
                    TrieNodeKey childKey = key.ChildGroup(slot);
                    ReadOnlySpan<int> childBuckets = ChildBuckets(precalculatedBuckets, slot);
                    bool childChanged;
                    PbtSubtreeStats childDelta;
                    if (occupants[slot].Kind == ResultKind.Internal)
                    {
                        // a stored child blob — group or run — descends with its own content
                        using RefCountingMemory? childData = store.GetTrieNode(childKey);
                        if (childData is not null) storedChildMask |= 1u << slot;
                        results[slot] = ApplyStored(childKey, bucket, childData, childBuckets, out childChanged, out childDelta);
                    }
                    else if (occupants[slot].Kind == ResultKind.Chain)
                    {
                        // the run ApplyChainSplit seeded below itself, which no key holds
                        results[slot] = ApplyChain(childKey, bucket, occupants[slot].ChainData, StoredBlob.None, childBuckets, out childChanged, out childDelta);
                    }
                    else
                    {
                        // an empty subtree or a lone stem: push the occupant (stem or absent) on down
                        results[slot] = ApplyPushed(childKey, bucket, occupants[slot], childBuckets, out childChanged, out childDelta);
                    }

                    if (childChanged) changedMask |= 1u << slot;
                    Debug.Assert(childChanged || childDelta.IsZero, "an unchanged subtree is the same subtree, so it holds the same stems");
                    childDeltas += childDelta;
                }

                ResultKind resultKind = results[slot].Kind;
                if (resultKind != ResultKind.Absent) occupied |= 1u << slot;
                if (resultKind == ResultKind.Stem) stems |= 1u << slot;
                if (resultKind == ResultKind.Chain) chainMask |= 1u << slot;
            }

            bool isRoot = depth == 0;
            NodeKind rootKind = KindOf(occupied, stems);

            // Every stem of this subtree sits under one of the sixteen slots, so what they hoisted is the
            // whole of what changed here. This holds however the nodes are settled below: the statistics
            // count the subtree's content, and none of removing, collapsing or rebuilding moves a stem.
            delta = childDeltas;
            PbtSubtreeStats afterStats = beforeStats + childDeltas;

            // A group left with one internal child is a run of single-child levels: five nodes that all
            // change whenever its one leaf does, and that nothing reads on their own. It stores as a
            // PbtNodeChain instead, merged with any run below so that a run is always as long as it can be.
            // The root group is exempt, as it is for a lone stem: it has no parent to hoist into.
            if (rootKind == NodeKind.Internal && !isRoot && BitOperations.PopCount(occupied) == 1)
            {
                changed = CollapseToChain(key, results, occupied, touchedMask, storedChildMask, beforeHash, afterStats, out NodeResult chain);
                Release(results, handedUp: -1);
                return chain;
            }

            // A group that folds to nothing, or to a lone stem hoisting into the parent, encodes to
            // nothing: there is no blob to rebuild, only one to remove. The hoisted result is handed
            // straight up, taking its lease with it.
            if (rootKind != NodeKind.Internal && !(isRoot && rootKind == NodeKind.Stem))
            {
                int hoistedSlot = rootKind == NodeKind.Absent ? -1 : BitOperations.TrailingZeroCount(occupied);
                NodeResult hoisted = hoistedSlot < 0 ? default : results[hoistedSlot];
                changed = hoisted.NodeHash() != beforeHash;
                if (stored != StoredBlob.None) store.SetTrieNode(key, null);
                Release(results, hoistedSlot);
                return hoisted;
            }

            // Every touched child came back unchanged, so the rebuild would reproduce the stored encoding
            // byte for byte: a child that changed kind would have reported changed, leaving `occupied` and
            // `stems` — hence the shape — those of `existing`, every hash the fold would cache is the one
            // already encoded, and an unchanged child hash is an unchanged subtree, so the statistics in
            // the header hoist nothing either. That makes the rewrite below a no-op too, which its guard
            // concludes only after building the encoding it then drops. Runs are the exception: one still
            // pending has no blob until this frame plants it.
            if (changedMask == 0 && chainMask == 0 && stored == StoredBlob.Group)
            {
                Debug.Assert(existing.KindAt(PbtTrieNodeGroup.RootPosition) == rootKind, "unchanged children reproduce the stored group's shape");
                Debug.Assert(childDeltas.IsZero, "an unchanged child hash is an unchanged subtree, which moves no statistic");
                changed = false;
                Release(results, handedUp: -1);
                existingData!.AcquireLease();
                return new NodeResult(
                    new NodeRef(ToResultKind(rootKind), existing.EntryOffset(PbtTrieNodeGroup.RootPosition)), existingData);
            }

            (NodeRef root, RefCountingMemory memory) = RebuildGroup(results, existing, occupied, stems, changedMask, afterStats);

            // Unchanged root => the encoding is byte-identical to what is stored (an internal root
            // whose hash matches implies the same subtree, hence the same cached boundary hashes); skip
            // the rewrite. A run stored here is never that: it is a spine, and this group branches.
            changed = PbtTrieNodeGroup.SlotAt(memory.GetSpan(), ToNodeKind(root.Kind), root.Offset).NodeHash() != beforeHash;

            // A run has no blob of its own until an ancestor keeps it: the frame that built it cannot know
            // whether this one plants it here or absorbs it four levels higher. This write overwrites
            // whatever the key held — a group that has just collapsed, an older run, or nothing.
            for (uint pending = chainMask; pending != 0; pending &= pending - 1)
            {
                int slot = BitOperations.TrailingZeroCount(pending);
                results[slot].PlantAt(store, key.ChildGroup(slot));
            }

            Release(results, handedUp: -1);

            // The write takes a lease with it; the result keeps its own, to read the root back from.
            if (changed || stored != StoredBlob.Group)
            {
                memory.AcquireLease();
                store.SetTrieNode(key, memory);
            }

            return new NodeResult(root, memory);
        }

        /// <summary>
        /// Folds a group left with the single occupied child <paramref name="results"/> into the run that
        /// replaces it, and reports whether that leaves the frame's root node changed. The frame's own key
        /// is left untouched: its consumer decides whether to plant the run there or absorb it.
        /// </summary>
        /// <param name="stats">
        /// What the group's subtree amounts to now. One occupied slot holds all of it, so it is the
        /// surviving child's as much as the group's, and the run replacing them reaches exactly it —
        /// which is what spares the read below from having to fetch it.
        /// </param>
        private bool CollapseToChain(
            in TrieNodeKey key, ReadOnlySpan<NodeResult> results, uint occupied, uint touchedMask, uint storedChildMask,
            in ValueHash256 beforeHash, in PbtSubtreeStats stats, out NodeResult chain)
        {
            int slot = BitOperations.TrailingZeroCount(occupied);
            TrieNodeKey childKey = key.ChildGroup(slot);
            NodeResult survivor = results[slot].Lease();
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
                    survivor = new NodeResult(new NodeRef(ResultKind.Chain, 0), childData);
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
        private static void Release(Span<NodeResult> nodes, int handedUp)
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
        /// Whether the internal node at <paramref name="position"/>, over the boundary slots
        /// <paramref name="range"/>, folds to exactly the nodes <paramref name="existing"/> already
        /// encodes — so that its entries are copied out of that encoding rather than rebuilt.
        /// </summary>
        /// <remarks>
        /// The fold is a pure function of a range's boundary results, and every stored group is some
        /// fold's output, so a range whose results are the ones this very group was built from folds
        /// back to the bytes already there. That is what <paramref name="changed"/> says: an untouched
        /// slot's result is the occupant routed straight out of <paramref name="existing"/>, and a
        /// touched one that reports unchanged came back the same node — the same kind, hash and stem —
        /// since a change of any of them moves the node hash <c>changed</c> is decided on.
        /// <para>
        /// Whether the slot's blob is a group or a run does not enter into it: a run's cached node hash
        /// is a boundary internal's hash, so the entry either way is the same 32 bytes, and the blob
        /// itself is settled outside the encoding. A frame with no existing encoding — a pushed-stem
        /// subtree or a run — has nothing to copy, and a single slot is one entry either way.
        /// </para>
        /// </remarks>
        /// <param name="format">
        /// The encoding being written. A run is only ever what a fold in that very format produced, so a
        /// group in the other one is refolded in full instead — which is what converts it on the way
        /// past. A skipped position heads no run either: it has no entry, and
        /// <see cref="PbtTrieNodeGroup.Builder.AppendSubtree"/> takes a run's last entry for its root.
        /// Its children are stored, so they are copied in its place and it is folded from them.
        /// </param>
        private static bool IsCleanRange(
            PbtTrieNodeGroup existing, uint changed, uint range, int position, int width, PbtGroupFormat format) =>
            width > 1
            && (changed & range) == 0
            && !existing.IsEmpty
            && existing.Format == format
            && PbtTrieNodeGroup.StoresInternalAtWidth(format, width)
            && existing.KindAt(position) == NodeKind.Internal;

        /// <summary>
        /// Rebuilds a group's sixteen boundary <paramref name="results"/> straight into a fresh
        /// encoding of the group.
        /// </summary>
        /// <returns>
        /// The encoding, and its root node as an offset into it — for the caller to read back and
        /// then either store or drop, whichever its root turns out to call for. The caller takes the
        /// lease on the memory with it.
        /// </returns>
        /// <param name="stats">What the whole subtree amounts to, for the group's header; see <see cref="PbtTrieNodeGroup.Builder.Finish"/>.</param>
        private (NodeRef Root, RefCountingMemory Memory) RebuildGroup(
            ReadOnlySpan<NodeResult> results, PbtTrieNodeGroup existing, uint occupied, uint stems, uint changedMask,
            in PbtSubtreeStats stats)
        {
            (uint presence, uint stemMask) = PredictShape(existing, occupied, stems, changedMask, PbtTrieNodeGroup.RootPosition, 0, PbtTrieNodeGroup.BoundarySlots, writeFormat);
            RefCountingMemory memory = memoryProvider.Rent(PbtTrieNodeGroup.EncodedLength(presence, stemMask));

            GroupRebuild rebuild = new(results, existing, occupied, stems, changedMask, memory.GetSpan(), writeFormat);
            // the group root is stored whatever the format: the caller reads it back out of the encoding
            NodeRef root = rebuild.Fold(PbtTrieNodeGroup.RootPosition, 0, PbtTrieNodeGroup.BoundarySlots).ToNodeRef();
            int length = rebuild.Finish(stats);
            Debug.Assert(length == memory.GetSpan().Length, "the predicted shape must size the fold's encoding exactly");

            return (root, memory);
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
                    // a stem terminates the walk at its shortest unique prefix — nothing below it is
                    // emitted, and it is stored there whatever the format
                    return (1u << position, 1u << position);
            }

            if (IsCleanRange(existing, changed, range, position, width, format))
            {
                existing.SubtreeBitmaps(position, width, out uint cleanPresence, out uint cleanStems);
                return (cleanPresence, cleanStems);
            }

            // an internal node the format skips is folded, not stored, so it takes no room
            uint self = PbtTrieNodeGroup.StoresInternalAtWidth(format, width) ? 1u << position : 0;
            if (width == 1) return (self, 0);

            int half = width / 2;
            (uint leftPresence, uint leftStems) = PredictShape(existing, occupied, stems, changed, position - width, firstSlot, half, format);
            (uint rightPresence, uint rightStems) = PredictShape(existing, occupied, stems, changed, position - 1, firstSlot + half, half, format);
            return (leftPresence | rightPresence | self, leftStems | rightStems);
        }

        /// <summary>A reference to a node in the encoding holding it: its kind and its entry offset.</summary>
        private readonly record struct NodeRef(ResultKind Kind, int Offset);

        /// <summary>
        /// A node the rebuild has folded, as its parent needs it: a <see cref="NodeRef"/> into the
        /// encoding being built where the group stores an entry for it, and its hash by value where
        /// the group stores none.
        /// </summary>
        /// <remarks>
        /// A <see cref="PbtGroupFormat.Interleaved"/> group stores no internal node at an odd level, so
        /// there is no entry for its parent to read the hash back out of — and the hash is the only
        /// thing about it that survives the encoding. It is carried here rather than on
        /// <see cref="NodeRef"/>, which stays a bare reference: only the fold ever holds an unstored
        /// node, and only until the parent one frame up consumes it. An absent node is unstored with a
        /// zero hash, which is exactly what it contributes.
        /// </remarks>
        private readonly record struct FoldedNode(ResultKind Kind, int Offset, ValueHash256 Hash)
        {
            private const int NoEntry = -1;

            public static FoldedNode Absent => new(ResultKind.Absent, NoEntry, default);

            public static FoldedNode Stored(ResultKind kind, int offset) => new(kind, offset, default);

            public static FoldedNode Unstored(in ValueHash256 hash) => new(ResultKind.Internal, NoEntry, hash);

            public bool IsStored => Offset != NoEntry;

            /// <summary>This node as a bare reference, for a consumer that reads it back out of the encoding.</summary>
            public NodeRef ToNodeRef()
            {
                Debug.Assert(IsStored, "an unstored node is the fold's own business: it has no entry to point at");
                return new NodeRef(Kind, Offset);
            }
        }

        /// <summary>
        /// A node handed around the descent: where its bytes are, and a lease on the memory holding
        /// them. Absent nodes hold nothing, so <c>default</c> is one.
        /// </summary>
        /// <remarks>
        /// A node always lives in some encoding, and the result borrows it rather than copying it out:
        /// an existing group's node in the group's own blob, a rebuilt group's root in the encoding
        /// just built, and a stem node built at its shortest unique prefix — which no group holds yet,
        /// its bytes being a stem and the root of a leaf blob merkelized moments earlier — in memory of
        /// its own. A run is the same, its encoding being a <see cref="PbtNodeChain"/> no key holds
        /// until an ancestor plants it. The lease is what makes the node safe to hand up: it outlives
        /// the buffer's other owners, whether that is a <c>using</c> in a frame the node is passed out
        /// of, or an <see cref="IPbtStore"/> write taking the encoding with it.
        /// </remarks>
        private readonly struct NodeResult(NodeRef node, RefCountingMemory? memory) : IDisposable
        {
            private readonly NodeRef _node = node;
            private readonly RefCountingMemory? _memory = memory;

            public ResultKind Kind => _node.Kind;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Stem"/>
            public Stem Stem => Slot.Stem;

            /// <summary>A run's encoding, borrowed from the memory this result leases.</summary>
            public ReadOnlySpan<byte> ChainData
            {
                get
                {
                    Debug.Assert(_node.Kind == ResultKind.Chain && _node.Offset == 0, "only a run is an encoding all of its own");
                    return _memory!.GetSpan();
                }
            }

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Hash"/>
            /// <remarks>A run's cached node hash: what it contributes to its parent, as an internal node's is.</remarks>
            public ValueHash256 Hash => _node.Kind == ResultKind.Chain ? PbtNodeChain.NodeHashOf(ChainData) : Slot.Hash;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.NodeHash"/>
            public ValueHash256 NodeHash() => _node.Kind == ResultKind.Chain ? PbtNodeChain.NodeHashOf(ChainData) : Slot.NodeHash();

            /// <summary>
            /// Hands this run's encoding to <paramref name="store"/> as the blob at <paramref name="key"/>,
            /// taking a lease for the store to release and keeping this result's own.
            /// </summary>
            public readonly void PlantAt(IPbtStore store, in TrieNodeKey key)
            {
                Debug.Assert(_node.Kind == ResultKind.Chain, "only a run is planted; every other node is written by the frame that built it");
                _memory!.AcquireLease();
                store.SetTrieNode(key, _memory);
            }

            /// <summary>The node itself, borrowed from the memory this result leases.</summary>
            /// <remarks>
            /// <see cref="PbtTrieNodeGroup.Slot"/> tells a node's kind by its entry's length, so a run's
            /// encoding would read as a stem here; <see cref="ToNodeKind"/> rejects one instead.
            /// </remarks>
            private PbtTrieNodeGroup.Slot Slot =>
                _memory is null ? default : PbtTrieNodeGroup.SlotAt(_memory.GetSpan(), ToNodeKind(_node.Kind), _node.Offset);

            /// <summary>Takes a second lease on the node, for another owner to release in its own time.</summary>
            public NodeResult Lease()
            {
                _memory?.AcquireLease();
                return this;
            }

            public void Dispose() => ((IDisposable?)_memory)?.Dispose();
        }

        /// <summary>
        /// Rebuilds a group's nodes from its sixteen boundary results directly into the group's final
        /// encoding.
        /// </summary>
        /// <remarks>
        /// Mirrors <see cref="StemLeafBlob.RebuildState"/>: a single post-order walk appends each node
        /// to the blob as it is folded, with no intermediate set of nodes and no separate encoding
        /// pass. This is well-defined because post-order visits a group's positions in ascending
        /// order — exactly the order the encoding stores them in — so each entry's offset is implicit
        /// in the append cursor, and a subtree's entries are one unbroken run. That run is what lets
        /// the walk prune at a range no write changed and copy it over whole, as the leaf blob's
        /// rebuild copies a clean leaf subtree. The walk hands each subtree up as a
        /// <see cref="FoldedNode"/>, a reference into the buffer, so a node's hash is materialized only
        /// where its parent needs it rather than copied by value up the stack — bar a node the format
        /// stores no entry for, whose hash has nowhere else to live.
        /// <para>
        /// Node kinds come from <see cref="KindOf"/> rather than from the walk, which is what lets a
        /// node be appended at its own position instead of by its parent: a stem never recurses, so
        /// every node reached below the root has an internal parent and is therefore materialized.
        /// The two shapes that are not — an empty group and a stem hoisting out of a non-root group —
        /// encode to nothing and are settled by the caller before the walk starts.
        /// </para>
        /// </remarks>
        private ref struct GroupRebuild(
            ReadOnlySpan<NodeResult> results, PbtTrieNodeGroup existing,
            uint occupied, uint stems, uint changed, Span<byte> destination, PbtGroupFormat format)
        {
            private readonly ReadOnlySpan<NodeResult> _results = results;
            private readonly PbtTrieNodeGroup _existing = existing;
            private readonly uint _occupied = occupied;
            private readonly uint _stems = stems;
            private readonly uint _changed = changed;
            private readonly PbtGroupFormat _format = format;
            private PbtTrieNodeGroup.Builder _builder = new(destination, format);

            /// <summary>
            /// Folds the boundary results over <c>[firstSlot, firstSlot + width)</c> into the node at
            /// <paramref name="position"/>, appending it and every node below it to the blob.
            /// </summary>
            public FoldedNode Fold(int position, int firstSlot, int width)
            {
                uint range = ((1u << width) - 1) << firstSlot;
                uint occupied = _occupied & range;
                switch (KindOf(occupied, _stems))
                {
                    case NodeKind.Absent:
                        return FoldedNode.Absent;
                    case NodeKind.Stem:
                        // the topmost frame whose range holds only this stem — its shortest unique
                        // prefix, so it lands here rather than anywhere below
                        ref readonly NodeResult stem = ref _results[BitOperations.TrailingZeroCount(occupied)];
                        return FoldedNode.Stored(ResultKind.Stem, _builder.AppendStem(position, stem.Stem, stem.Hash));
                }

                // An unchanged subtree folds to the nodes already encoded, so the whole of its run — its
                // cached hashes and every node below them — is copied out of the existing group in one
                // block, never walked. Mirrors StemLeafBlob.RebuildState's clean-subtree copy.
                if (IsCleanRange(_existing, _changed, range, position, width, _format))
                {
                    _existing.SubtreeBitmaps(position, width, out uint presence, out uint stems);
                    return FoldedNode.Stored(
                        ResultKind.Internal,
                        _builder.AppendSubtree(presence, stems, _existing.SubtreeEntries(position, width, presence, stems)));
                }

                // a boundary slot: its internal node is the cached pointer to the child blob, which for a
                // run is the run's own cached node hash — so a run reads here exactly as a group does
                if (width == 1) return FoldedNode.Stored(ResultKind.Internal, _builder.AppendInternal(position, _results[firstSlot].Hash));

                int half = width / 2;
                FoldedNode left = Fold(position - width, firstSlot, half);
                FoldedNode right = Fold(position - 1, firstSlot + half, half);
                ValueHash256 hash = Blake3Hash.HashPairOrZero(Resolve(left), Resolve(right));

                // The hash is folded either way — the root depends on every level of it. A format that
                // skips this level simply does not write it down, and hands it to the parent that needs
                // it instead.
                return PbtTrieNodeGroup.StoresInternalAtWidth(_format, width)
                    ? FoldedNode.Stored(ResultKind.Internal, _builder.AppendInternal(position, hash))
                    : FoldedNode.Unstored(hash);
            }

            /// <summary>The hash <paramref name="node"/> contributes to its parent.</summary>
            public readonly ValueHash256 Resolve(in FoldedNode node) =>
                node.IsStored ? _builder.SlotAt(ToNodeKind(node.Kind), node.Offset).NodeHash() : node.Hash;

            /// <inheritdoc cref="PbtTrieNodeGroup.Builder.Finish"/>
            public readonly int Finish(in PbtSubtreeStats stats) => _builder.Finish(stats);
        }

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
        /// stem bits at that depth: bucket i is <paramref name="bounds"/>[i]..[i+1]. Mirrors
        /// <c>PatriciaTree.BulkSet</c>'s per-level bucket sort — no global sort is needed, and within-bucket
        /// order is arbitrary since each bucket is re-partitioned by its child group.
        /// </summary>
        private static void Partition(Span<PbtWriteBatch.StemEntry> entries, int depth, Span<int> bounds)
        {
            // Only the levels the producer left unbucketed reach here, which for a batch drained from
            // PbtWriteBatchBuilder — its shard key handing over the depth-0 and depth-4 bounds — is
            // everything below depth 8, by which point the batch has fragmented sixteen ways twice and a
            // range is almost always one or a few entries. The count-and-permute below costs its
            // sixteen-wide passes whether it buckets one entry or a hundred, so the tiny ranges that
            // dominate take a sort network instead.
            if (entries.Length <= TinyRange)
            {
                SortTiny(entries, depth, bounds);
                return;
            }

            Span<int> counts = stackalloc int[PbtTrieNodeGroup.BoundarySlots];
            counts.Clear();
            for (int i = 0; i < entries.Length; i++) counts[NibbleOf(entries[i].Stem, depth)]++;

            int total = 0;
            for (int bucket = 0; bucket < PbtTrieNodeGroup.BoundarySlots; bucket++)
            {
                bounds[bucket] = total;
                total += counts[bucket];
            }
            bounds[PbtTrieNodeGroup.BoundarySlots] = total;
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
        private static void SortTiny(Span<PbtWriteBatch.StemEntry> entries, int depth, Span<int> bounds)
        {
            Debug.Assert(!entries.IsEmpty && entries.Length <= TinyRange);

            Span<int> nibbleBuffer = stackalloc int[TinyRange];
            Span<int> nibbles = nibbleBuffer[..entries.Length];
            for (int i = 0; i < nibbles.Length; i++) nibbles[i] = NibbleOf(entries[i].Stem, depth);

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
