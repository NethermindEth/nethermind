// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

/// <summary>Checks every witness node reachable from the root of a trie the block decodes against its depth.</summary>
/// <remarks>
/// Mirrors <c>_validate_secured_trie_paths</c> of the zkEVM spec's <c>incremental_mpt.py</c>, which checks every witness
/// node reachable from a trie's root once it decodes that trie: the state trie for every block, and a storage trie once
/// its old storage is read, or written without being cleared first. Here these are the scope opening and the first load
/// of a storage tree's root, which a cleared storage tree never loads. A trie operation visits only nodes reachable from
/// that root, which the walk has checked by then. A child the witness lacks is left unchecked, as the spec leaves it hashed.
/// The walks run where <see cref="Walks"/> says: on the host and the Airbender guest. The other guests skip them, as the
/// walk decodes every witness node once more and costs a third of a mainnet block's steps on ZisK.
/// </remarks>
internal sealed partial class WitnessTriePaths
{
    private const int BranchChildren = 16;

    private readonly INodeStorage _nodes;
    // The host commits storage tries in parallel.
    private readonly Lock _lock = new();
    // Storage roots whose trees the block opened; pending until loaded, then walked once.
    private readonly OptimizedHashSet<ValueHash256> _storageRoots = [];
    private readonly OptimizedHashSet<ValueHash256> _pendingStorageRoots = [];

    // Whether a node passes depends on it and its depth alone, so once the walks have read twice the witness' bytes, which
    // takes nodes reached along several paths, each pair is walked once. Otherwise a witness could reference one node from
    // every child of a branch, and the walk would grow exponentially with the depth. Twice, as the tries of a block can
    // share a few nodes.
    private long _bytesBeforeMemo;
    private OptimizedHashSet<WalkedNode>? _walked;

    /// <param name="nodes">The witness' nodes, keyed by keccak; a load from it checks nothing.</param>
    /// <param name="state">The same nodes, as the witness lists them.</param>
    public WitnessTriePaths(INodeStorage nodes, ReadOnlySpan<byte[]> state)
    {
        _nodes = nodes;
        if (!Walks) return;

        foreach (byte[] node in state) _bytesBeforeMemo += 2 * node.Length;
    }

    /// <summary>Walks the state trie at <paramref name="root"/>.</summary>
    /// <exception cref="TrieException">A node's path does not end at the key, or its list does not fill its bytes.</exception>
    public void ValidateStateTrie(Hash256 root)
    {
        if (!Walks) return;

        lock (_lock) Validate(root.ValueHash256);
    }

    /// <summary>Notes the root of a storage tree the block opened, to walk once the tree loads it.</summary>
    public void AddStorageRoot(Hash256 root)
    {
        if (!Walks || root == Keccak.EmptyTreeHash) return;

        lock (_lock)
        {
            if (_storageRoots.Add(root.ValueHash256)) _pendingStorageRoots.Add(root.ValueHash256);
        }
    }

    /// <summary>Walks the storage trie at <paramref name="keccak"/> if it is a noted root not yet loaded.</summary>
    /// <remarks>Called ahead of every load of a storage trie's node, so only a pending root leaves the inlined check.</remarks>
    /// <exception cref="TrieException">A node's path does not end at the key, or its list does not fill its bytes.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnStorageNodeLoading(in ValueHash256 keccak)
    {
        if (Walks && _pendingStorageRoots.Count != 0) OnPendingStorageNodeLoading(keccak);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OnPendingStorageNodeLoading(ValueHash256 keccak)
    {
        lock (_lock)
        {
            if (_pendingStorageRoots.Remove(keccak)) Validate(keccak);
        }
    }

    private void Validate(in ValueHash256 root)
    {
        byte[]? rlp = _nodes.Get(null, TreePath.Empty, root);
        if (rlp is not null) WalkHashed(root, rlp, 0);
    }

    private void WalkHashed(in ValueHash256 keccak, byte[] rlp, int depth)
    {
        if ((_bytesBeforeMemo -= rlp.Length) < 0 && !(_walked ??= []).Add(new WalkedNode(keccak, depth))) return;

        Walk(rlp, depth);
    }

    /// <remarks>
    /// A node that is neither a leaf nor an extension is walked as a branch, of which only the children are read. The trie
    /// decoder reads a node's items up to the end of its bytes, so a list ending elsewhere could give it a child the walk
    /// did not reach.
    /// </remarks>
    private void Walk(ReadOnlySpan<byte> rlp, int depth)
    {
        SecuredTriePath.ValidateAtDepth(rlp, depth);
        if (!SecuredTriePath.TryReadItem(rlp, 0, out bool isList, out int position, out int end) || end != rlp.Length) ThrowListDoesNotFillNode();
        // Not a list: the empty node.
        if (!isList) return;

        if (SecuredTriePath.TryGetPath(rlp, out int nibbles, out bool isLeaf))
        {
            if (!isLeaf && SecuredTriePath.TryReadItem(rlp, position, out _, out _, out int pathEnd)) WalkChild(rlp, pathEnd, depth + nibbles);
            return;
        }

        for (int i = 0; i < BranchChildren && position < end; i++)
        {
            position = WalkChild(rlp, position, depth + 1);
        }
    }

    /// <summary>Walks the child of <paramref name="rlp"/> at <paramref name="position"/>: inlined, or a keccak the witness may hold.</summary>
    /// <returns>Where the child ends, or the end of <paramref name="rlp"/> if it cannot be read.</returns>
    private int WalkChild(ReadOnlySpan<byte> rlp, int position, int depth)
    {
        if (!SecuredTriePath.TryReadItem(rlp, position, out bool isList, out int start, out int end)) return rlp.Length;

        if (isList)
        {
            Walk(rlp[position..end], depth);
        }
        else if (end - start == Hash256.Size)
        {
            ValueHash256 keccak = new(rlp[start..end]);
            byte[]? child = _nodes.Get(null, TreePath.Empty, keccak);
            if (child is not null) WalkHashed(keccak, child, depth);
        }

        return end;
    }

    [DoesNotReturn]
    private static void ThrowListDoesNotFillNode() =>
        throw new TrieException("A witness trie node's list does not end at the end of its bytes");

    private readonly record struct WalkedNode(ValueHash256 Hash, int Depth);
}
