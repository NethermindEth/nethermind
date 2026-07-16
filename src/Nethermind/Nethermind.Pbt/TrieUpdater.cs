// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;
using ValueSlot = Nethermind.Pbt.PbtTrieNodeGroup.ValueSlot;

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
/// stored as 4-level <see cref="PbtTrieNodeGroup"/> tiles.
/// </summary>
/// <remarks>
/// The stem trie is the canonical binary trie of the stem set: an internal node exists at every
/// path prefix shared by two or more stems, and each stem node sits at the shortest prefix unique
/// to it — the EIP's minimal-internal-node rule. The batch is applied bulk-set style (mirroring
/// <c>PatriciaTree.BulkSet</c>): the write entries — one per stem, already grouped by the producer —
/// are never globally sorted; instead each group radix-partitions its own range in place into the
/// sixteen boundary slots by the four stem bits at its depth, so a single recursive descent walks
/// every shared prefix only once. A range that
/// collapses to a single stem folds that stem's
/// leaf blob and hands the stem node up to be placed at its shortest unique prefix by the bottom-up
/// rebuild of the enclosing groups; hashes are computed on the way back up. Groups and blobs are
/// read/written through <see cref="IPbtStore"/>; untouched child groups are never read or written.
/// </remarks>
public static class TrieUpdater
{
    /// <summary>
    /// Applies <paramref name="changes"/> (each entry a 32-byte tree key → value; an empty value
    /// clears the leaf) to the tree rooted at <paramref name="currentRoot"/>, writing the new leaf
    /// blobs and trie node groups to <paramref name="store"/>, and returns the new root (32 zero
    /// bytes for an empty tree). An empty batch returns <paramref name="currentRoot"/> untouched.
    /// </summary>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch changes, IRefCountingMemoryProvider memoryProvider) =>
        changes.Count == 0 ? currentRoot : new Updater(store, memoryProvider).Run(changes);

    private sealed class Updater(IPbtStore store, IRefCountingMemoryProvider memoryProvider)
    {
        public ValueHash256 Run(PbtWriteBatch changes)
        {
            // No global sort: each group radix-partitions its own range in place during the descent.
            using RefCountingMemory? rootData = store.GetTrieNode(TrieNodeKey.Root);
            using NodeResult root = ApplyGroup(TrieNodeKey.Root, changes.Entries,
                rootData is null ? default : PbtTrieNodeGroup.Decode(rootData.GetSpan()), out _);
            return root.NodeHash();
        }

        /// <summary>
        /// Applies <paramref name="entries"/> — a non-empty range of one-per-stem writes sharing bits
        /// <c>[0, key.Depth)</c> in any order — to the stored group at <paramref name="key"/> given its
        /// current <paramref name="existing"/> content (empty only at the root, when the tree has no
        /// stored root group yet). Writes or removes every affected group blob at or below
        /// <paramref name="key"/> and returns the node now occupying the group's root position for the
        /// parent's boundary slot. A stem result is not written here: it hoists into the parent,
        /// cascading across group boundaries (except at the root group, whose root may hold a stem).
        /// The caller disposes the result.
        /// </summary>
        /// <param name="changed"><inheritdoc cref="ApplyToOccupants" path="/param[@name='changed']"/></param>
        private NodeResult ApplyGroup(in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, PbtTrieNodeGroup existing, out bool changed)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty && depth % PbtTrieNodeGroup.LevelsPerGroup == 0);
            Debug.Assert(depth <= PbtTrieNodeGroup.MaxGroupDepth);

            // Route the existing occupants to their boundary slots for the rebuild: a stem anywhere in
            // the group goes to the slot its path passes through at `depth` (the rebuild recomputes its
            // position), and a boundary internal — the cached pointer to a child group — to its own slot.
            // Inner internals are recomputed or copied by the rebuild and are skipped. An empty group
            // reads as all-absent, seeding nothing.
            Span<ValueSlot> occupants = stackalloc ValueSlot[PbtTrieNodeGroup.BoundarySlots];
            occupants.Clear();
            for (int position = 0; position < PbtTrieNodeGroup.PositionCount; position++)
            {
                PbtTrieNodeGroup.Slot slot = existing[position];
                if (slot.Kind == NodeKind.Stem)
                {
                    int bucket = NibbleOf(slot.Stem, depth);
                    Debug.Assert(occupants[bucket].Kind == NodeKind.Absent, "two occupants routed to one boundary slot");
                    occupants[bucket] = slot.ToValue();
                }
                else if (slot.Kind == NodeKind.Internal && PbtTrieNodeGroup.IsBoundaryPosition(position))
                {
                    occupants[PbtTrieNodeGroup.BoundarySlot(position)] = slot.ToValue();
                }
            }

            return ApplyToOccupants(key, entries, existing, occupants, existing[PbtTrieNodeGroup.RootPosition].ToValue(), out changed);
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to a subtree with no stored group at <paramref name="key"/>:
        /// either empty, or holding a single stem <paramref name="pushed"/> down from the parent's
        /// boundary slot. A range holding one uncontended stem has reached the shortest prefix no other
        /// stem shares, so that stem's node is built here, out of its leaf blob, without descending;
        /// otherwise the pushed stem and the writes are routed on down together.
        /// </summary>
        /// <param name="changed"><inheritdoc cref="ApplyToOccupants" path="/param[@name='changed']"/></param>
        private NodeResult ApplyPushed(in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in ValueSlot pushed, out bool changed)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty && depth % PbtTrieNodeGroup.LevelsPerGroup == 0);
            Debug.Assert(pushed.Kind != NodeKind.Internal);

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
                bool isEmpty = ComputeBlob(stem, entries[0].Changes, out ValueHash256 subtreeRoot);
                ValueSlot stemNode = isEmpty ? default : PbtTrieNodeGroup.StemSlot(stem, subtreeRoot);
                changed = stemNode.NodeHash() != pushed.NodeHash();
                return new NodeResult(stemNode);
            }

            Debug.Assert(depth <= PbtTrieNodeGroup.MaxGroupDepth);

            Span<ValueSlot> occupants = stackalloc ValueSlot[PbtTrieNodeGroup.BoundarySlots];
            occupants.Clear();
            if (pushed.Kind == NodeKind.Stem) occupants[NibbleOf(pushed.Stem, depth)] = pushed;

            return ApplyToOccupants(key, entries, default, occupants, pushed, out changed);
        }

        /// <summary>
        /// The shared descent of <see cref="ApplyGroup"/> and <see cref="ApplyPushed"/>: with the group's
        /// boundary <paramref name="occupants"/> already seeded, partitions <paramref name="entries"/> to
        /// their slots, applies each touched slot's bucket to its child, then either removes the group
        /// (when it folds to nothing or to a lone hoisting stem) or rebuilds and writes it. Returns the
        /// node now occupying the group's root position.
        /// </summary>
        /// <param name="existing">
        /// The group's stored content, or the empty group when there is none (a pushed-stem subtree, or a
        /// tree with no stored root group). Used to reuse cached hashes in the rebuild and to decide
        /// whether a stored blob must be removed.
        /// </param>
        /// <param name="before">The node previously at the root position, against which <paramref name="changed"/> is decided.</param>
        /// <param name="changed">
        /// Set to <c>false</c> when the writes leave this group's root node identical to <paramref name="before"/>
        /// (all writes were no-ops), letting the parent reuse its cached hash and skip its own rewrite;
        /// <c>true</c> otherwise.
        /// </param>
        private NodeResult ApplyToOccupants(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, PbtTrieNodeGroup existing,
            Span<ValueSlot> occupants, in ValueSlot before, out bool changed)
        {
            int depth = key.Depth;

            Span<int> bounds = stackalloc int[PbtTrieNodeGroup.BoundarySlots + 1];
            Partition(entries, depth, bounds);

            // The boundary results drive both the rebuild and its shape: `occupied` and `stems` fix
            // every node's kind (see GroupRebuild), and `changedMask` folds to a range's
            // subtree-changed flag by masking. Each result leases the encoding its node lives in, so
            // every one of them is released before this frame returns, bar the node handed up.
            RefList16<NodeResult> resultBuffer = new(PbtTrieNodeGroup.BoundarySlots);
            Span<NodeResult> results = resultBuffer.AsSpan();
            uint occupied = 0;
            uint stems = 0;
            uint changedMask = 0;
            for (int slot = 0; slot < PbtTrieNodeGroup.BoundarySlots; slot++)
            {
                Span<PbtWriteBatch.StemEntry> bucket = entries[bounds[slot]..bounds[slot + 1]];
                if (bucket.IsEmpty)
                {
                    // untouched: reuse the cached boundary hash / stem, never loading the child group
                    results[slot] = new NodeResult(occupants[slot]);
                }
                else if (occupants[slot].Kind == NodeKind.Internal)
                {
                    // a stored child group descends with its own content, no stem pushed down
                    TrieNodeKey childKey = key.ChildGroup(slot);
                    using RefCountingMemory? childData = store.GetTrieNode(childKey);
                    results[slot] = ApplyGroup(childKey, bucket,
                        childData is null ? default : PbtTrieNodeGroup.Decode(childData.GetSpan()), out bool childChanged);
                    if (childChanged) changedMask |= 1u << slot;
                }
                else
                {
                    // an empty subtree or a lone stem: push the occupant (stem or absent) on down
                    results[slot] = ApplyPushed(key.ChildGroup(slot), bucket, occupants[slot], out bool childChanged);
                    if (childChanged) changedMask |= 1u << slot;
                }

                NodeKind resultKind = results[slot].Kind;
                if (resultKind != NodeKind.Absent) occupied |= 1u << slot;
                if (resultKind == NodeKind.Stem) stems |= 1u << slot;
            }

            // A group that folds to nothing, or to a lone stem hoisting into the parent, encodes to
            // nothing: there is no blob to rebuild, only one to remove. The hoisted result is handed
            // straight up, taking its lease with it.
            bool isRoot = depth == 0;
            NodeKind rootKind = KindOf(occupied, stems);
            if (rootKind != NodeKind.Internal && !(isRoot && rootKind == NodeKind.Stem))
            {
                int hoistedSlot = rootKind == NodeKind.Absent ? -1 : BitOperations.TrailingZeroCount(occupied);
                NodeResult hoisted = hoistedSlot < 0 ? default : results[hoistedSlot];
                changed = hoisted.NodeHash() != before.NodeHash();
                if (!existing.IsEmpty) store.SetTrieNode(key, null);
                ReleaseResults(results, hoistedSlot);
                return hoisted;
            }

            (NodeRef root, RefCountingMemory memory) = RebuildGroup(results, existing, occupied, stems, changedMask);
            ReleaseResults(results, handedUp: -1);

            // Unchanged root => the encoding is byte-identical to what is stored (an internal root
            // whose hash matches implies the group already existed); skip the rewrite.
            changed = PbtTrieNodeGroup.SlotAt(memory.GetSpan(), root.Kind, root.Offset).NodeHash() != before.NodeHash();

            // The write takes a lease with it; the result keeps its own, to read the root back from.
            if (changed)
            {
                memory.AcquireLease();
                store.SetTrieNode(key, memory);
            }

            return new NodeResult(root, memory);
        }

        /// <summary>
        /// Releases the boundary <paramref name="results"/>' leases once the group is settled, bar
        /// slot <paramref name="handedUp"/> — the node this frame returns, whose lease goes with it
        /// (<c>-1</c> when the frame returns a node of its own).
        /// </summary>
        private static void ReleaseResults(Span<NodeResult> results, int handedUp)
        {
            for (int slot = 0; slot < results.Length; slot++)
            {
                if (slot != handedUp) results[slot].Dispose();
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
        /// Rebuilds a group's sixteen boundary <paramref name="results"/> straight into a fresh
        /// encoding of the group.
        /// </summary>
        /// <returns>
        /// The encoding, and its root node as an offset into it — for the caller to read back and
        /// then either store or drop, whichever its root turns out to call for. The caller takes the
        /// lease on the memory with it.
        /// </returns>
        private (NodeRef Root, RefCountingMemory Memory) RebuildGroup(
            ReadOnlySpan<NodeResult> results, PbtTrieNodeGroup existing, uint occupied, uint stems, uint changedMask)
        {
            (uint presence, uint stemMask) = PredictShape(occupied, stems, PbtTrieNodeGroup.RootPosition, 0, PbtTrieNodeGroup.BoundarySlots);
            RefCountingMemory memory = memoryProvider.Rent(PbtTrieNodeGroup.EncodedLength(presence, stemMask));

            GroupRebuild rebuild = new(results, existing, occupied, stems, changedMask, memory.GetSpan());
            NodeRef root = rebuild.Fold(PbtTrieNodeGroup.RootPosition, 0, PbtTrieNodeGroup.BoundarySlots);
            int length = rebuild.Finish();
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
        /// leaf blob from its live-node counts. This is well-defined because a node's kind follows from
        /// <see cref="KindOf"/> over its range, which needs only the boundary masks.
        /// </remarks>
        private static (uint Presence, uint Stems) PredictShape(uint occupied, uint stems, int position, int firstSlot, int width)
        {
            uint range = ((1u << width) - 1) << firstSlot;
            switch (KindOf(occupied & range, stems))
            {
                case NodeKind.Absent:
                    return default;
                case NodeKind.Stem:
                    // a stem terminates the walk at its shortest unique prefix — nothing below it is emitted
                    return (1u << position, 1u << position);
            }

            uint self = 1u << position;
            if (width == 1) return (self, 0);

            int half = width / 2;
            (uint leftPresence, uint leftStems) = PredictShape(occupied, stems, position - width, firstSlot, half);
            (uint rightPresence, uint rightStems) = PredictShape(occupied, stems, position - 1, firstSlot + half, half);
            return (leftPresence | rightPresence | self, leftStems | rightStems);
        }

        /// <summary>A reference to a node appended to the new group blob: its kind and its entry offset.</summary>
        private readonly record struct NodeRef(NodeKind Kind, int Offset);

        /// <summary>
        /// A node handed up the descent, with a lease on whatever holds its bytes.
        /// </summary>
        /// <remarks>
        /// A rebuilt group's root lives in that group's encoding, so the result points at it and keeps
        /// the encoding alive — the parent reads the node straight out of it, and disposing the result
        /// releases it. The nodes that no encoding holds — an absent node, and a stem folded at its
        /// shortest unique prefix, which is synthesized from its blob's subtree root — are carried by
        /// value instead, and dispose to nothing.
        /// </remarks>
        private readonly struct NodeResult : IDisposable
        {
            private readonly ValueSlot _synthesized;
            private readonly RefCountingMemory? _memory;
            private readonly NodeRef _node;

            /// <summary>A node no encoding holds: absent, or a stem folded in place.</summary>
            public NodeResult(in ValueSlot slot) => _synthesized = slot;

            /// <summary>The node <paramref name="node"/> of <paramref name="memory"/>, taking its lease.</summary>
            public NodeResult(in NodeRef node, RefCountingMemory memory)
            {
                _node = node;
                _memory = memory;
            }

            public NodeKind Kind => _memory is null ? _synthesized.Kind : _node.Kind;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Stem"/>
            public Stem Stem => _memory is null ? _synthesized.Stem : Slot.Stem;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Hash"/>
            public ValueHash256 Hash => _memory is null ? _synthesized.Hash : Slot.Hash;

            /// <inheritdoc cref="PbtTrieNodeGroup.Slot.NodeHash"/>
            public ValueHash256 NodeHash() => _memory is null ? _synthesized.NodeHash() : Slot.NodeHash();

            private PbtTrieNodeGroup.Slot Slot => PbtTrieNodeGroup.SlotAt(_memory!.GetSpan(), _node.Kind, _node.Offset);

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
        /// in the append cursor. The walk hands each subtree up as a <see cref="NodeRef"/> into the
        /// buffer, so a node's hash is materialized only where its parent needs it, never copied by
        /// value up the stack.
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
            uint occupied, uint stems, uint changed, Span<byte> destination)
        {
            private readonly ReadOnlySpan<NodeResult> _results = results;
            private readonly PbtTrieNodeGroup _existing = existing;
            private readonly uint _occupied = occupied;
            private readonly uint _stems = stems;
            private readonly uint _changed = changed;
            private PbtTrieNodeGroup.Builder _builder = new(destination);

            /// <summary>
            /// Folds the boundary results over <c>[firstSlot, firstSlot + width)</c> into the node at
            /// <paramref name="position"/>, appending it and every node below it to the blob.
            /// </summary>
            public NodeRef Fold(int position, int firstSlot, int width)
            {
                uint range = ((1u << width) - 1) << firstSlot;
                uint occupied = _occupied & range;
                switch (KindOf(occupied, _stems))
                {
                    case NodeKind.Absent:
                        return default;
                    case NodeKind.Stem:
                        // the topmost frame whose range holds only this stem — its shortest unique
                        // prefix, so it lands here rather than anywhere below
                        ref readonly NodeResult stem = ref _results[BitOperations.TrailingZeroCount(occupied)];
                        return new NodeRef(NodeKind.Stem, _builder.AppendStem(position, stem.Stem, stem.Hash));
                }

                // a boundary slot: its internal node is the cached pointer to the child group
                if (width == 1) return new NodeRef(NodeKind.Internal, _builder.AppendInternal(position, _results[firstSlot].Hash));

                int half = width / 2;
                NodeRef left = Fold(position - width, firstSlot, half);
                NodeRef right = Fold(position - 1, firstSlot + half, half);

                // an unchanged subtree keeps its cached hash; its existing slot is internal by
                // construction then, as unchanged inputs reproduce the existing structure
                ValueHash256 hash = (_changed & range) == 0 && !_existing.IsEmpty && _existing[position].Kind == NodeKind.Internal
                    ? _existing[position].Hash
                    : Blake3Hash.HashPairOrZero(Resolve(left), Resolve(right));
                return new NodeRef(NodeKind.Internal, _builder.AppendInternal(position, hash));
            }

            /// <summary>The hash <paramref name="node"/> contributes to its parent.</summary>
            public readonly ValueHash256 Resolve(in NodeRef node) => _builder.SlotAt(node.Kind, node.Offset).NodeHash();

            /// <inheritdoc cref="PbtTrieNodeGroup.Builder.Finish"/>
            public readonly int Finish() => _builder.Finish();
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
        /// Radix-partitions <paramref name="entries"/> (sharing bits <c>[0, depth)</c>, any order) in place
        /// into the sixteen boundary buckets of the group at <paramref name="depth"/>, keyed by the four
        /// stem bits at that depth: bucket i is <paramref name="bounds"/>[i]..[i+1]. Mirrors
        /// <c>PatriciaTree.BulkSet</c>'s per-level bucket sort — no global sort is needed, and within-bucket
        /// order is arbitrary since each bucket is re-partitioned by its child group.
        /// </summary>
        private static void Partition(Span<PbtWriteBatch.StemEntry> entries, int depth, Span<int> bounds)
        {
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

        private static int NibbleOf(in Stem stem, int depth) => NibbleAt(stem.Bytes[depth >> 3], depth);

        private static int NibbleAt(byte value, int depth) => (depth & 4) == 0 ? value >> 4 : value & 0xF;
    }
}
