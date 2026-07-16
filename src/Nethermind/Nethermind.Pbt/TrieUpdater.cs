// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using Nethermind.Core.Crypto;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;
using Slot = Nethermind.Pbt.PbtTrieNodeGroup.Slot;

namespace Nethermind.Pbt;

/// <summary>
/// Backing store for the PBT tree: the stem trie node groups and the per-stem 256-leaf blobs. Reads
/// return a poolable, disposable wrapper (null = absent); writes take a span the store copies
/// (an empty span removes the group / deletes the stem).
/// </summary>
public interface IPbtStore
{
    MemoryManager<byte>? GetTrieNode(in TrieNodeKey key);
    void SetTrieNode(in TrieNodeKey key, ReadOnlySpan<byte> node);
    MemoryManager<byte>? GetLeafBlob(in Stem stem);
    void SetLeafBlob(in Stem stem, ReadOnlySpan<byte> blob);
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
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch changes) =>
        changes.Count == 0 ? currentRoot : new Updater(store).Run(changes);

    private sealed class Updater(IPbtStore store)
    {
        public ValueHash256 Run(PbtWriteBatch changes)
        {
            // No global sort: each group radix-partitions its own range in place during the descent.
            PbtWriteBatch.StemEntry[] entries = changes.Entries.ToArray();
            using MemoryManager<byte>? rootData = store.GetTrieNode(TrieNodeKey.Root);
            return ApplyGroup(TrieNodeKey.Root, entries, Wrap(rootData), default).NodeHash();
        }

        private static PbtTrieNodeGroup Wrap(MemoryManager<byte>? data) =>
            data is null ? default : PbtTrieNodeGroup.Decode(data.GetSpan());

        /// <summary>
        /// Applies <paramref name="entries"/> — a non-empty range of one-per-stem writes sharing bits
        /// <c>[0, key.Depth)</c> in any order — to the group at <paramref name="key"/>,
        /// given its current <paramref name="existing"/> content or a stem <paramref name="pushed"/>
        /// down from the parent's boundary slot (mutually exclusive: a boundary stem implies no child
        /// group). Writes or removes every affected group blob at or below <paramref name="key"/> and
        /// returns the node now occupying the group's root position for the parent's boundary slot.
        /// A stem result is not written here: it hoists into the parent, cascading across group
        /// boundaries (except at the root group, whose root position may hold a stem).
        /// </summary>
        private Slot ApplyGroup(in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, PbtTrieNodeGroup existing, in Slot pushed)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty && depth % PbtTrieNodeGroup.LevelsPerGroup == 0);
            Debug.Assert(existing.IsEmpty || pushed.Kind == NodeKind.Absent);

            bool isRoot = depth == 0;
            if (existing.IsEmpty && !isRoot)
            {
                // Shortest unique prefix reached at or above this group: an empty subtree, or the
                // very stem being updated (possibly relocating). Fold the blob and hand the stem
                // node up without descending; an enclosing fold places it. This also serves depth
                // 248, where every remaining range is necessarily a single stem.
                Stem stem = entries[0].Stem;
                if (entries.Length == 1 && (pushed.Kind == NodeKind.Absent || pushed.Stem == stem))
                {
                    bool isEmpty = ComputeBlob(stem, entries[0].Changes, out ValueHash256 subtreeRoot);
                    return isEmpty ? default : PbtTrieNodeGroup.StemSlot(stem, subtreeRoot);
                }
            }

            Debug.Assert(depth <= PbtTrieNodeGroup.MaxGroupDepth);

            // Route the current occupants to their boundary slots: a stem anywhere in the group goes
            // to the slot its path passes through (the fold below recomputes its position); a
            // boundary internal is the cached pointer to a child group; inner internals are
            // recomputed or copied by the fold.
            Span<Slot> occupants = stackalloc Slot[PbtTrieNodeGroup.BoundarySlots];
            occupants.Clear();
            if (!existing.IsEmpty)
            {
                for (int position = 0; position < PbtTrieNodeGroup.PositionCount; position++)
                {
                    Slot slot = existing[position];
                    if (slot.Kind == NodeKind.Stem)
                    {
                        int bucket = NibbleOf(slot.Stem, depth);
                        Debug.Assert(occupants[bucket].Kind == NodeKind.Absent, "two occupants routed to one boundary slot");
                        occupants[bucket] = slot;
                    }
                    else if (slot.Kind == NodeKind.Internal && PbtTrieNodeGroup.IsBoundaryPosition(position))
                    {
                        occupants[PbtTrieNodeGroup.BoundarySlot(position)] = slot;
                    }
                }
            }
            else if (pushed.Kind == NodeKind.Stem)
            {
                occupants[NibbleOf(pushed.Stem, depth)] = pushed;
            }

            Span<int> bounds = stackalloc int[PbtTrieNodeGroup.BoundarySlots + 1];
            Partition(entries, depth, bounds);

            Span<Slot> results = stackalloc Slot[PbtTrieNodeGroup.BoundarySlots];
            Span<bool> changed = stackalloc bool[PbtTrieNodeGroup.BoundarySlots];
            changed.Clear();
            for (int slot = 0; slot < PbtTrieNodeGroup.BoundarySlots; slot++)
            {
                Span<PbtWriteBatch.StemEntry> bucket = entries[bounds[slot]..bounds[slot + 1]];
                if (bucket.IsEmpty)
                {
                    // untouched: reuse the cached boundary hash / stem, never loading the child group
                    results[slot] = occupants[slot];
                    continue;
                }

                changed[slot] = true;
                TrieNodeKey childKey = key.ChildGroup(slot);
                if (occupants[slot].Kind == NodeKind.Internal)
                {
                    using MemoryManager<byte>? childData = store.GetTrieNode(childKey);
                    results[slot] = ApplyGroup(childKey, bucket, Wrap(childData), default);
                }
                else
                {
                    results[slot] = ApplyGroup(childKey, bucket, default, occupants[slot]);
                }
            }

            Span<Slot> output = stackalloc Slot[PbtTrieNodeGroup.PositionCount];
            output.Clear();
            Slot root = Fold(PbtTrieNodeGroup.RootPosition, 0, PbtTrieNodeGroup.BoundarySlots, results, changed, existing, output, out _);

            if (root.Kind == NodeKind.Internal || (isRoot && root.Kind == NodeKind.Stem))
            {
                output[PbtTrieNodeGroup.RootPosition] = root;
                WriteGroup(key, output);
            }
            else if (!existing.IsEmpty)
            {
                // collapsed to empty or to a lone stem hoisting into the parent: remove the blob
                store.SetTrieNode(key, default);
            }

            return root;
        }

        /// <summary>
        /// Rebuilds the group's inner positions bottom-up from the sixteen boundary results,
        /// materializing child slots into <paramref name="output"/> and enforcing the canonical
        /// shape: a position with no children collapses to empty, and a lone stem child is never
        /// materialized — it propagates up and lands at the first position where its sibling is
        /// non-empty, its shortest unique prefix (possibly in an ancestor group).
        /// </summary>
        private static Slot Fold(
            int position, int firstSlot, int width,
            ReadOnlySpan<Slot> results, ReadOnlySpan<bool> changed,
            PbtTrieNodeGroup existing, Span<Slot> output, out bool subtreeChanged)
        {
            if (width == 1)
            {
                subtreeChanged = changed[firstSlot];
                return results[firstSlot];
            }

            int half = width / 2;
            Slot left = Fold(position - width, firstSlot, half, results, changed, existing, output, out bool leftChanged);
            Slot right = Fold(position - 1, firstSlot + half, half, results, changed, existing, output, out bool rightChanged);
            subtreeChanged = leftChanged || rightChanged;

            if (left.Kind == NodeKind.Absent && right.Kind == NodeKind.Absent) return default;
            if (left.Kind == NodeKind.Stem && right.Kind == NodeKind.Absent) return left;
            if (right.Kind == NodeKind.Stem && left.Kind == NodeKind.Absent) return right;

            output[position - width] = left;
            output[position - 1] = right;

            // an unchanged subtree keeps its cached hash; its existing slot is internal by
            // construction then, as unchanged inputs reproduce the existing structure
            ValueHash256 hash = !subtreeChanged && !existing.IsEmpty && existing[position].Kind == NodeKind.Internal
                ? existing[position].Hash
                : Blake3Hash.HashPairOrZero(left.NodeHash(), right.NodeHash());
            return PbtTrieNodeGroup.InternalSlot(hash);
        }

        /// <summary>Folds one stem's writes (<paramref name="changes"/>) into its leaf blob, persists it, and reports whether the stem is now empty.</summary>
        private bool ComputeBlob(in Stem stem, IPbtStemChanges changes, out ValueHash256 subtreeRoot)
        {
            using MemoryManager<byte>? prior = store.GetLeafBlob(stem);
            using StemLeafBlob.RebuildState newBlob = StemLeafBlob.Apply(prior is null ? default : prior.GetSpan(), changes);
            subtreeRoot = newBlob.SubtreeRoot;
            store.SetLeafBlob(stem, newBlob.Blob);
            return newBlob.IsEmpty;
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

        private void WriteGroup(in TrieNodeKey key, ReadOnlySpan<Slot> output)
        {
            Span<byte> encoded = stackalloc byte[PbtTrieNodeGroup.MaxEncodedLength];
            store.SetTrieNode(key, encoded[..PbtTrieNodeGroup.Encode(output, encoded)]);
        }
    }
}
