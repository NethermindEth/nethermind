// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class TrieChangeCollector
{
    private readonly List<(TreePath Path, TrieNode Node)> _changed = [];

    public void Collect(TrieNode? root, int maxDepth)
    {
        _changed.Clear();
        if (root is null) return;

        TreePath path = TreePath.Empty;
        Visit(root, ref path, maxDepth);
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
    }

    private void Visit(TrieNode node, ref TreePath path, int maxDepth)
    {
        if (node.Keccak is not null || path.Length > maxDepth) return;

        _changed.Add((path, node));
        if (node.IsLeaf || path.Length == maxDepth) return;

        if (node.IsExtension)
        {
            if (!node.TryGetDirtyChild(0, out TrieNode? child)) return;

            int length = path.Length;
            path.AppendMut(node.Key!);
            Visit(child, ref path, maxDepth);
            path.TruncateMut(length);
            return;
        }

        int parentLength = path.Length;
        path.AppendMut(0);
        for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
        {
            if (!node.TryGetDirtyChild(nibble, out TrieNode? child)) continue;

            path.SetLast(nibble);
            Visit(child, ref path, maxDepth);
        }

        path.TruncateMut(parentLength);
    }
}
