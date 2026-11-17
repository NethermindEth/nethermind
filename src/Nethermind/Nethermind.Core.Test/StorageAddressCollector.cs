// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Core.Test;

/// <summary>Collects the account address that the visit context reports for each storage trie leaf.</summary>
public sealed class StorageAddressCollector : ITreeVisitor<TreePathContextWithStorage>
{
    private readonly HashSet<Hash256> _addresses = [];

    public IReadOnlyCollection<Hash256> Addresses => _addresses;

    public bool IsFullDbScan => true;

    public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => true;

    public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }

    public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) =>
        throw new InvalidOperationException($"Missing node {nodeHash}");

    public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) { }

    public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) { }

    public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node)
    {
        if (nodeContext.Storage is null) return;
        lock (_addresses) _addresses.Add(nodeContext.Storage);
    }

    public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) { }
}
