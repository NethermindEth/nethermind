// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Pbt;

public interface IStemTrieNodeSource
{
    byte[]? GetTrieNode(in TrieNodeKey key);
}

/// <summary>
/// The top-level EIP-8297 binary trie over stems. Leaf subtrees are opaque here: a stem node
/// carries only the 256-leaf subtree root computed by <see cref="StemLeafBlob"/>.
/// </summary>
/// <remarks>
/// The structure is the canonical binary trie of the stem set: an internal node exists at every
/// path prefix shared by two or more stems, and each stem node sits at the shortest prefix unique
/// to it — the EIP's minimal-internal-node rule, maintained by splitting on insert and by
/// hoisting a lone sibling stem on delete. One instance performs one batch over a node source;
/// nodes read or written are kept in an overlay so the batch never re-reads the source.
/// </remarks>
public class StemTrie(IStemTrieNodeSource source)
{
    private readonly Dictionary<TrieNodeKey, StemTrieNode?> _overlay = [];
    private readonly HashSet<TrieNodeKey> _dirty = [];
    private readonly List<TrieNodeKey> _walkedPath = [];

    /// <summary>
    /// Applies all stem changes (null = delete the stem), recomputes affected hashes bottom-up,
    /// emits every changed node into <paramref name="dirtyNodesOut"/> (null = node removed) and
    /// returns the new root hash (32 zero bytes for an empty tree).
    /// </summary>
    public ValueHash256 BatchUpdate(IReadOnlyDictionary<Stem, ValueHash256?> stemChanges, IDictionary<TrieNodeKey, byte[]?> dirtyNodesOut)
    {
        foreach ((Stem stem, ValueHash256? leafSubtreeRoot) in stemChanges)
        {
            if (leafSubtreeRoot is { } subtreeRoot)
            {
                Insert(stem, subtreeRoot);
            }
            else
            {
                Delete(stem);
            }
        }

        return ComputeHashes(dirtyNodesOut);
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

    private ValueHash256 ComputeHashes(IDictionary<TrieNodeKey, byte[]?> dirtyNodesOut)
    {
        List<TrieNodeKey> keys = [.. _dirty];
        keys.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

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

            dirtyNodesOut[key] = node?.Encode();
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

        byte[]? data = source.GetTrieNode(key);
        node = data is null ? null : StemTrieNode.Decode(data);
        _overlay[key] = node;
        return node;
    }
}
