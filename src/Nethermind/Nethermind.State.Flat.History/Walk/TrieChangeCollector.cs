// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class TrieChangeCollector
{
    private readonly List<(TreePath Path, TrieNode Node)> _changed = [];
    private int _deepestReached;

    public int DeepestReached => _deepestReached;

    public void Collect(TrieNode? root, int maxDepth) => Collect(root, maxDepth, resolver: null);

    public void CollectAll(TrieNode? root, int maxDepth, ITrieNodeResolver resolver) => Collect(root, maxDepth, resolver);

    private void Collect(TrieNode? root, int maxDepth, ITrieNodeResolver? resolver)
    {
        _changed.Clear();
        _deepestReached = 0;
        if (root is null) return;

        TreePath path = TreePath.Empty;
        Visit(root, ref path, maxDepth, resolver);
    }

    public void RecordAccounts(CommitmentEmitter emitter, int minRecordedDepth)
    {
        foreach ((TreePath path, TrieNode node) in _changed)
        {
            if (path.Length >= minRecordedDepth) emitter.RecordAccountNode(path, node.FullRlp.AsSpan());
        }
    }

    public void RecordStorage(CommitmentEmitter emitter, in ValueHash256 accountPath, int minRecordedDepth)
    {
        foreach ((TreePath path, TrieNode node) in _changed)
        {
            if (path.Length >= minRecordedDepth) emitter.RecordStorageNode(accountPath, path, node.FullRlp.AsSpan());
        }

        emitter.RecordStorageDepthReached(accountPath, _deepestReached);
    }

    private void Visit(TrieNode node, ref TreePath path, int maxDepth, ITrieNodeResolver? resolver)
    {
        if (path.Length > _deepestReached) _deepestReached = path.Length;
        if ((resolver is null && node.Keccak is not null) || path.Length > maxDepth) return;

        _changed.Add((path, node));
        if (node.IsLeaf) return;

        if (path.Length == maxDepth)
        {
            if (maxDepth + 1 > _deepestReached) _deepestReached = maxDepth + 1;
            return;
        }

        if (node.IsExtension)
        {
            if (!TryGetChild(node, ref path, 0, resolver, out TrieNode? child)) return;

            int length = path.Length;
            path.AppendMut(node.Key!);
            Visit(child, ref path, maxDepth, resolver);
            path.TruncateMut(length);
            return;
        }

        int parentLength = path.Length;
        for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
        {
            if (!TryGetChild(node, ref path, nibble, resolver, out TrieNode? child)) continue;

            path.AppendMut(nibble);
            Visit(child, ref path, maxDepth, resolver);
            path.TruncateMut(parentLength);
        }
    }

    private static bool TryGetChild(TrieNode node, ref TreePath parentPath, int index, ITrieNodeResolver? resolver, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TrieNode? child)
    {
        if (resolver is null) return node.TryGetDirtyChild(index, out child);

        child = node.GetChild(resolver, ref parentPath, index);
        return child is not null;
    }
}
