// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// Backing store for the PBT tree: the stem trie nodes and the per-stem 256-leaf blobs. Reads
/// return a poolable, disposable wrapper (null = absent); writes take a span the store copies
/// (an empty span removes the node / deletes the stem).
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
/// stem's leaf blob (<see cref="StemLeafBlob"/>), then maintains the top-level binary trie of stems.
/// </summary>
/// <remarks>
/// The stem trie is the canonical binary trie of the stem set: an internal node exists at every
/// path prefix shared by two or more stems, and each stem node sits at the shortest prefix unique
/// to it — the EIP's minimal-internal-node rule. The batch is applied bulk-set style (mirroring
/// <c>PatriciaTree.BulkSet</c>): the write entries are sorted once by key and a single recursive
/// descent partitions them into the two child subtrees at each node, so every shared prefix is
/// walked only once. A range that collapses to a single stem folds that stem's leaf blob and places
/// the stem node; hashes are computed on the way back up. Nodes and blobs are read/written through
/// <see cref="IPbtStore"/>.
/// </remarks>
public static class TrieUpdater
{
    /// <summary>
    /// Applies <paramref name="changes"/> (each entry a 32-byte tree key → value; an empty value
    /// clears the leaf) to the tree rooted at <paramref name="currentRoot"/>, writing the new leaf
    /// blobs and trie nodes to <paramref name="store"/>, and returns the new root (32 zero bytes for
    /// an empty tree). An empty batch returns <paramref name="currentRoot"/> untouched.
    /// </summary>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch changes) =>
        changes.Count == 0 ? currentRoot : new Updater(store).Run(changes);

    private sealed class Updater(IPbtStore store)
    {
        // Reused scratch for one stem's sub-index writes; a base case fully consumes it before the
        // next (DFS visits leaves one at a time), so a single buffer cleared per stem suffices.
        private readonly Dictionary<byte, ValueHash256> _stemChanges = [];

        public ValueHash256 Run(PbtWriteBatch changes)
        {
            PbtWriteBatch.Entry[] entries = SortedDistinctEntries(changes);
            StemTrieNode? root = Apply(TrieNodeKey.Root, entries, GetNode(TrieNodeKey.Root));
            WriteNode(TrieNodeKey.Root, root);
            return root?.ComputeHash() ?? default;
        }

        /// <summary>
        /// Applies <paramref name="entries"/> — a non-empty range of distinct-key writes sharing bits
        /// <c>[0, key.Depth)</c>, sorted ascending by key — to the subtree at <paramref name="key"/>
        /// given its current <paramref name="existing"/> node. Writes every node strictly below
        /// <paramref name="key"/> at its final position and returns the node that now occupies
        /// <paramref name="key"/> (null = empty) for the caller to write.
        /// </summary>
        private StemTrieNode? Apply(in TrieNodeKey key, ReadOnlySpan<PbtWriteBatch.Entry> entries, StemTrieNode? existing)
        {
            Stem stem = StemOf(entries[0]);
            if (stem == StemOf(entries[^1]))
            {
                // Shortest unique prefix reached: an empty slot, or the very stem being updated. Fold
                // the blob and place the stem node here without descending further.
                if (existing is null)
                {
                    byte[] blob = ComputeBlob(stem, entries, out ValueHash256 subtreeRoot);
                    return blob.Length == 0 ? null : StemTrieNode.StemNode(stem, subtreeRoot);
                }

                if (existing.IsStem && existing.Stem == stem)
                {
                    byte[] blob = ComputeBlob(stem, entries, out ValueHash256 subtreeRoot);
                    if (blob.Length == 0) return null;
                    existing.LeafSubtreeRoot = subtreeRoot;
                    return existing;
                }

                // A different existing stem, or an existing internal subtree: fall through and branch.
            }

            int depth = key.Depth;
            TrieNodeKey leftKey = key.Child(0);
            TrieNodeKey rightKey = key.Child(1);

            // An existing stem is pushed down to its own side and must be re-written at its new,
            // deeper key even if no batch entries land there; an existing internal node's children
            // stay in place, so an untouched side is left as-is (read only for the hash it carries up).
            StemTrieNode? existingLeft, existingRight;
            bool leftRelocate = false, rightRelocate = false;
            if (existing is null)
            {
                existingLeft = existingRight = null;
            }
            else if (existing.IsStem)
            {
                bool existingGoesLeft = existing.Stem.GetBit(depth) == 0;
                existingLeft = existingGoesLeft ? existing : null;
                existingRight = existingGoesLeft ? null : existing;
                leftRelocate = existingGoesLeft;
                rightRelocate = !existingGoesLeft;
            }
            else
            {
                existingLeft = GetNode(leftKey);
                existingRight = GetNode(rightKey);
            }

            int split = PartitionIndex(entries, depth);
            ReadOnlySpan<PbtWriteBatch.Entry> leftEntries = entries[..split];
            ReadOnlySpan<PbtWriteBatch.Entry> rightEntries = entries[split..];

            bool leftHasEntries = !leftEntries.IsEmpty;
            bool rightHasEntries = !rightEntries.IsEmpty;
            StemTrieNode? newLeft = leftHasEntries ? Apply(leftKey, leftEntries, existingLeft) : existingLeft;
            StemTrieNode? newRight = rightHasEntries ? Apply(rightKey, rightEntries, existingRight) : existingRight;

            return Combine(leftKey, rightKey, newLeft, newRight, leftHasEntries || leftRelocate, rightHasEntries || rightRelocate);
        }

        /// <summary>
        /// Assembles the node for a position from its two recomputed children, writing each changed
        /// child at its key and enforcing the canonical shape: a position with no children collapses
        /// to empty; one left with a lone stem child hoists that stem up (returned for the caller to
        /// place one level higher, cascading the hoist); a lone internal child stays a canonical
        /// shared-prefix internal.
        /// </summary>
        private StemTrieNode? Combine(
            in TrieNodeKey leftKey, in TrieNodeKey rightKey,
            StemTrieNode? left, StemTrieNode? right,
            bool leftChanged, bool rightChanged)
        {
            if (left is not null && right is not null)
            {
                if (leftChanged) WriteNode(leftKey, left);
                if (rightChanged) WriteNode(rightKey, right);
                StemTrieNode node = StemTrieNode.Internal();
                node.LeftHash = left.ComputeHash();
                node.RightHash = right.ComputeHash();
                return node;
            }

            if (left is null && right is null)
            {
                if (leftChanged) WriteNode(leftKey, null);
                if (rightChanged) WriteNode(rightKey, null);
                return null;
            }

            bool loneIsLeft = left is not null;
            StemTrieNode lone = (left ?? right)!;
            TrieNodeKey loneKey = loneIsLeft ? leftKey : rightKey;
            TrieNodeKey otherKey = loneIsLeft ? rightKey : leftKey;
            bool loneChanged = loneIsLeft ? leftChanged : rightChanged;
            bool otherChanged = loneIsLeft ? rightChanged : leftChanged;

            if (otherChanged) WriteNode(otherKey, null);

            if (lone.IsStem)
            {
                // a lone stem child is the forbidden shape: hoist it into this position instead
                WriteNode(loneKey, null);
                return lone;
            }

            if (loneChanged) WriteNode(loneKey, lone);
            StemTrieNode single = StemTrieNode.Internal();
            if (loneIsLeft) single.LeftHash = lone.ComputeHash();
            else single.RightHash = lone.ComputeHash();
            return single;
        }

        /// <summary>Folds one stem's writes (<paramref name="stemEntries"/>) into its leaf blob, persists it, and returns it.</summary>
        private byte[] ComputeBlob(in Stem stem, ReadOnlySpan<PbtWriteBatch.Entry> stemEntries, out ValueHash256 subtreeRoot)
        {
            _stemChanges.Clear();
            for (int i = 0; i < stemEntries.Length; i++)
            {
                _stemChanges[stemEntries[i].Key.Bytes[Stem.Length]] = stemEntries[i].Value;
            }

            using MemoryManager<byte>? prior = store.GetLeafBlob(stem);
            byte[] newBlob = StemLeafBlob.Apply(prior is null ? default : prior.GetSpan(), _stemChanges, out subtreeRoot);
            store.SetLeafBlob(stem, newBlob);
            return newBlob;
        }

        /// <summary>
        /// The index in <paramref name="entries"/> (sorted ascending by key) of the first entry whose
        /// stem bit at <paramref name="depth"/> is 1 — the boundary between the two child subtrees.
        /// </summary>
        private static int PartitionIndex(ReadOnlySpan<PbtWriteBatch.Entry> entries, int depth)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (BitOf(entries[i], depth) == 1) return i;
            }

            return entries.Length;
        }

        /// <summary>Collapses duplicate keys (last-write-wins) and returns the entries sorted ascending by key.</summary>
        private static PbtWriteBatch.Entry[] SortedDistinctEntries(PbtWriteBatch changes)
        {
            Dictionary<ValueHash256, ValueHash256> byKey = new(changes.Count);
            ReadOnlySpan<PbtWriteBatch.Entry> entries = changes.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                byKey[entries[i].Key] = entries[i].Value;
            }

            PbtWriteBatch.Entry[] sorted = new PbtWriteBatch.Entry[byKey.Count];
            int index = 0;
            foreach ((ValueHash256 key, ValueHash256 value) in byKey)
            {
                sorted[index++] = new PbtWriteBatch.Entry(key, value);
            }

            Array.Sort(sorted, static (a, b) => a.Key.CompareTo(b.Key));
            return sorted;
        }

        /// <summary>The 31-byte stem of an entry's 32-byte tree key.</summary>
        private static Stem StemOf(in PbtWriteBatch.Entry entry) => new(entry.Key.Bytes[..Stem.Length]);

        /// <summary>The entry's stem bit at <paramref name="depth"/>, MSB-first (matching <see cref="Stem.GetBit"/>).</summary>
        private static int BitOf(in PbtWriteBatch.Entry entry, int depth) =>
            (entry.Key.Bytes[depth >> 3] >> (7 - (depth & 7))) & 1;

        private void WriteNode(in TrieNodeKey key, StemTrieNode? node)
        {
            if (node is null)
            {
                // an empty span removes the node
                store.SetTrieNode(key, default);
                return;
            }

            Span<byte> encoded = stackalloc byte[StemTrieNode.MaxEncodedLength];
            store.SetTrieNode(key, encoded[..node.Encode(encoded)]);
        }

        private StemTrieNode? GetNode(in TrieNodeKey key)
        {
            using MemoryManager<byte>? data = store.GetTrieNode(key);
            return data is null ? null : StemTrieNode.Decode(data.GetSpan());
        }
    }
}
