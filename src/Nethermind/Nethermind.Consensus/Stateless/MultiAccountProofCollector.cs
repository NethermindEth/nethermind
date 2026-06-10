// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Walks the state trie once and captures trie-node RLP along the path to each of N target accounts,
/// replacing N independent per-account <see cref="State.Proofs.AccountProofCollector"/> walks for
/// the state-trie portion. Storage tries are walked separately — one
/// <see cref="State.Proofs.AccountProofCollector"/> per account — because the visitor framework's
/// <c>ctx.Storage</c> is a state-trie path commitment, not the address hash a discriminator would need.
/// </summary>
internal sealed class MultiAccountProofCollector : ITreeVisitor<TreePathContextWithStorage>
{
    private readonly ValueHash256[] _accountHashes;
    private readonly List<byte[]> _nodes;

    public IReadOnlyList<byte[]> Nodes => _nodes;

    public MultiAccountProofCollector(IReadOnlyDictionary<AddressAsKey, HashSet<UInt256>> storageSlots)
    {
        int n = storageSlots.Count;
        _accountHashes = new ValueHash256[n];
        int i = 0;
        int totalSlots = 0;
        foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> entry in storageSlots)
        {
            _accountHashes[i++] = ValueKeccak.Compute(entry.Key.Value.Bytes);
            totalSlots += entry.Value.Count;
        }

        // Approximate capacity: a short branch/extension/leaf chain per touched account and slot.
        _nodes = new List<byte[]>(Math.Max(16, (n + totalSlots) * 2));
    }

    public bool IsFullDbScan => false;

    public bool ShouldVisit(in TreePathContextWithStorage ctx, in ValueHash256 nextNode)
    {
        // State trie only: storage tries are walked by the per-account AccountProofCollector pass.
        if (ctx.Storage is not null) return false;
        for (int i = 0; i < _accountHashes.Length; i++)
        {
            ReadOnlySpan<byte> accountHash = _accountHashes[i].Bytes;
            if (IsPrefix(accountHash, ctx.Path)) return true;
        }
        return false;
    }

    public void VisitTree(in TreePathContextWithStorage ctx, in ValueHash256 rootHash) { }

    public void VisitMissingNode(in TreePathContextWithStorage ctx, in ValueHash256 nodeHash) { }

    public void VisitBranch(in TreePathContextWithStorage ctx, TrieNode node) => AddProofItem(node);

    public void VisitExtension(in TreePathContextWithStorage ctx, TrieNode node) => AddProofItem(node);

    public void VisitLeaf(in TreePathContextWithStorage ctx, TrieNode node) => AddProofItem(node);

    public void VisitAccount(in TreePathContextWithStorage ctx, TrieNode node, in AccountStruct account) { }

    private void AddProofItem(TrieNode node)
    {
        if (node.Keccak is null) return;
        _nodes.Add(node.FullRlp.ToArray());
    }

    private static bool IsPrefix(ReadOnlySpan<byte> target, in TreePath currentPath)
    {
        int length = currentPath.Length;
        if (length > target.Length * 2) return false;
        for (int i = 0; i < length; i++)
        {
            int targetNibble = (i & 1) == 0 ? target[i >> 1] >> 4 : target[i >> 1] & 0x0F;
            if (currentPath[i] != targetNibble) return false;
        }
        return true;
    }
}
