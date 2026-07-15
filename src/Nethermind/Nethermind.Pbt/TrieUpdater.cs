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
/// to it — the EIP's minimal-internal-node rule, maintained by splitting on insert and by hoisting
/// a lone sibling stem on delete. Nodes and blobs are read/written through <see cref="IPbtStore"/>;
/// nodes touched in a batch are kept in a per-call overlay so the store is never re-read.
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
        private readonly Dictionary<TrieNodeKey, StemTrieNode?> _overlay = [];
        private readonly HashSet<TrieNodeKey> _dirty = [];
        private readonly List<TrieNodeKey> _walkedPath = [];

        public ValueHash256 Run(PbtWriteBatch changes)
        {
            foreach ((Stem stem, Dictionary<byte, ValueHash256> subChanges) in GroupByStem(changes))
            {
                using MemoryManager<byte>? prior = store.GetLeafBlob(stem);
                byte[] newBlob = StemLeafBlob.Apply(prior is null ? default : prior.GetSpan(), subChanges, out ValueHash256 subtreeRoot);
                store.SetLeafBlob(stem, newBlob);

                if (newBlob.Length == 0)
                {
                    Delete(stem);
                }
                else
                {
                    Insert(stem, subtreeRoot);
                }
            }

            return ComputeHashes();
        }

        /// <summary>Groups the batch by stem, keeping the last value per (stem, sub-index) — the last-write-wins the dict used to give.</summary>
        private static Dictionary<Stem, Dictionary<byte, ValueHash256>> GroupByStem(PbtWriteBatch changes)
        {
            Dictionary<Stem, Dictionary<byte, ValueHash256>> byStem = [];
            ReadOnlySpan<PbtWriteBatch.Entry> entries = changes.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                ref readonly PbtWriteBatch.Entry entry = ref entries[i];
                ReadOnlySpan<byte> key = entry.Key.Bytes;
                Stem stem = new(key[..Stem.Length]);
                if (!byStem.TryGetValue(stem, out Dictionary<byte, ValueHash256>? subChanges))
                {
                    byStem[stem] = subChanges = [];
                }

                subChanges[key[Stem.Length]] = entry.Value;
            }

            return byStem;
        }

        private void Insert(in Stem stem, in ValueHash256 leafSubtreeRoot)
        {
            _walkedPath.Clear();
            TrieNodeKey key = TrieNodeKey.Root;
            while (true)
            {
                StemTrieNode? node = GetNode(key);
                if (node is null)
                {
                    _overlay[key] = StemTrieNode.StemNode(stem, leafSubtreeRoot);
                    MarkPathAndKeyDirty(key);
                    return;
                }

                if (node.IsStem)
                {
                    if (node.Stem == stem)
                    {
                        node.LeafSubtreeRoot = leafSubtreeRoot;
                    }
                    else
                    {
                        Split(key, node, stem, leafSubtreeRoot);
                    }

                    MarkPathAndKeyDirty(key);
                    return;
                }

                _walkedPath.Add(key);
                key = key.Child(stem.GetBit(key.Depth));
            }
        }

        /// <summary>
        /// Replaces the stem node at <paramref name="key"/> with a chain of internal nodes covering
        /// the bits the two stems share from <paramref name="key"/> down to their first differing bit,
        /// then places both stem nodes below the divergence.
        /// </summary>
        private void Split(in TrieNodeKey key, StemTrieNode existing, in Stem stem, in ValueHash256 leafSubtreeRoot)
        {
            int divergence = stem.FirstDifferingBit(existing.Stem, key.Depth);
            TrieNodeKey current = key;
            while (true)
            {
                _overlay[current] = StemTrieNode.Internal();
                _dirty.Add(current);
                if (current.Depth == divergence) break;
                current = current.Child(stem.GetBit(current.Depth));
            }

            TrieNodeKey newKey = current.Child(stem.GetBit(divergence));
            TrieNodeKey movedKey = current.Child(existing.Stem.GetBit(divergence));
            _overlay[newKey] = StemTrieNode.StemNode(stem, leafSubtreeRoot);
            _overlay[movedKey] = existing;
            _dirty.Add(newKey);
            _dirty.Add(movedKey);
        }

        private void Delete(in Stem stem)
        {
            _walkedPath.Clear();
            TrieNodeKey key = TrieNodeKey.Root;
            while (true)
            {
                StemTrieNode? node = GetNode(key);
                if (node is null) return;
                if (node.IsStem)
                {
                    if (node.Stem != stem) return;
                    _overlay[key] = null;
                    MarkPathAndKeyDirty(key);
                    NormalizeUp();
                    return;
                }

                _walkedPath.Add(key);
                key = key.Child(stem.GetBit(key.Depth));
            }
        }

        /// <summary>
        /// Restores the canonical structure above a deletion: an internal node left with no children
        /// becomes empty, one left with a lone stem child is replaced by that stem node (hoisting it
        /// toward the root); a node keeping two children — or a lone internal child, which still
        /// separates stems below — ends the walk, as nothing above it can change either.
        /// </summary>
        private void NormalizeUp()
        {
            for (int i = _walkedPath.Count - 1; i >= 0; i--)
            {
                TrieNodeKey key = _walkedPath[i];
                TrieNodeKey leftKey = key.Child(0);
                TrieNodeKey rightKey = key.Child(1);
                StemTrieNode? left = GetNode(leftKey);
                StemTrieNode? right = GetNode(rightKey);

                if (left is null && right is null)
                {
                    _overlay[key] = null;
                    continue;
                }

                StemTrieNode? loneChild = left is null ? right : right is null ? left : null;
                if (loneChild is not { IsStem: true }) break;

                _overlay[key] = loneChild;
                TrieNodeKey loneChildKey = left is null ? rightKey : leftKey;
                _overlay[loneChildKey] = null;
                _dirty.Add(loneChildKey);
            }
        }

        private ValueHash256 ComputeHashes()
        {
            List<TrieNodeKey> keys = [.. _dirty];
            keys.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

            Span<byte> encoded = stackalloc byte[StemTrieNode.MaxEncodedLength];
            foreach (TrieNodeKey key in keys)
            {
                StemTrieNode? node = _overlay[key];
                if (key.Depth > 0)
                {
                    // A non-internal parent means this node was hoisted or emptied away; there is no
                    // child hash slot to update and the hash of this (now empty) position is zero.
                    TrieNodeKey parentKey = new((byte)(key.Depth - 1), ParentPath(key));
                    if (_overlay.TryGetValue(parentKey, out StemTrieNode? parent) && parent is { IsStem: false })
                    {
                        ValueHash256 hash = node?.ComputeHash() ?? default;
                        if (key.Path.GetBit(key.Depth - 1) == 0)
                        {
                            parent.LeftHash = hash;
                        }
                        else
                        {
                            parent.RightHash = hash;
                        }
                    }
                }

                // an empty span removes the node
                store.SetTrieNode(key, node is null ? default : encoded[..node.Encode(encoded)]);
            }

            return GetNode(TrieNodeKey.Root)?.ComputeHash() ?? default;
        }

        private static Stem ParentPath(in TrieNodeKey key)
        {
            Stem keyPath = key.Path;
            Span<byte> path = stackalloc byte[Stem.Length];
            keyPath.Bytes.CopyTo(path);
            int bit = key.Depth - 1;
            path[bit >> 3] &= (byte)~(1 << (7 - (bit & 7)));
            return new Stem(path);
        }

        private void MarkPathAndKeyDirty(in TrieNodeKey key)
        {
            foreach (TrieNodeKey walked in _walkedPath)
            {
                _dirty.Add(walked);
            }

            _dirty.Add(key);
        }

        private StemTrieNode? GetNode(in TrieNodeKey key)
        {
            if (_overlay.TryGetValue(key, out StemTrieNode? node)) return node;

            using MemoryManager<byte>? data = store.GetTrieNode(key);
            node = data is null ? null : StemTrieNode.Decode(data.GetSpan());
            _overlay[key] = node;
            return node;
        }
    }
}
