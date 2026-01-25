// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Xdc;

public sealed class TrieNodeCounter: ITreeVisitor<TreePathContextWithStorage>
{
    public int BranchCount { get; private set; }
    public int ExtensionCount { get; private set; }
    public int LeafCount { get; private set; }
    public int AccountCount { get; private set; }
    public int MissingCount { get; private set; }

    public bool IsFullDbScan => true;

    public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => true;
    public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }
    public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) => MissingCount++;
    public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) => BranchCount++;
    public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) => ExtensionCount++;
    public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node) => LeafCount++;
    public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) => AccountCount++;

    public override string ToString() =>
        $"Branch nodes: {BranchCount}, extension: {ExtensionCount}, leaf nodes: {LeafCount}, account: {AccountCount}, missing: {MissingCount}";
}
