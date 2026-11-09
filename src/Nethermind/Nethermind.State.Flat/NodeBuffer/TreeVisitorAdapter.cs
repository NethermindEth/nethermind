// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>Runs an <see cref="ITreeVisitor{TNodeContext}"/> as an <see cref="INodeRecordVisitor{TNodeContext}"/>, decoding a <see cref="TrieNode"/> from each record it is handed.</summary>
internal sealed class TreeVisitorAdapter<TNodeContext>(ITreeVisitor<TNodeContext> visitor) : INodeRecordVisitor<TNodeContext>
    where TNodeContext : struct, INodeContext<TNodeContext>
{
    public bool IsFullDbScan => visitor.IsFullDbScan;

    public bool ExpectAccounts => visitor.ExpectAccounts;

    public ReadFlags ExtraReadFlag => visitor.ExtraReadFlag;

    public bool ShouldVisit(in TNodeContext nodeContext, in ValueHash256 nextNode) => visitor.ShouldVisit(nodeContext, nextNode);

    public void VisitTree(in TNodeContext nodeContext, in ValueHash256 rootHash) => visitor.VisitTree(nodeContext, rootHash);

    public void VisitMissingNode(in TNodeContext nodeContext, in ValueHash256 nodeHash) => visitor.VisitMissingNode(nodeContext, nodeHash);

    public void VisitBranch(in TNodeContext nodeContext, NodeRecordRef node) => visitor.VisitBranch(nodeContext, TreeVisitorAdapter.ToTrieNode(node));

    public void VisitExtension(in TNodeContext nodeContext, NodeRecordRef node) => visitor.VisitExtension(nodeContext, TreeVisitorAdapter.ToTrieNode(node));

    public void VisitLeaf(in TNodeContext nodeContext, NodeRecordRef node) => visitor.VisitLeaf(nodeContext, TreeVisitorAdapter.ToTrieNode(node));

    public void VisitAccount(in TNodeContext nodeContext, NodeRecordRef node, in AccountStruct account) =>
        visitor.VisitAccount(nodeContext, TreeVisitorAdapter.ToTrieNode(node), account);
}

/// <summary>The part of <see cref="TreeVisitorAdapter{TNodeContext}"/> that does not depend on the node context, so it is compiled once.</summary>
internal static class TreeVisitorAdapter
{
    /// <remarks>An embedded node has no hash, as in <see cref="PatriciaTree.Accept{TNodeContext}"/>.</remarks>
    public static TrieNode ToTrieNode(NodeRecordRef record)
    {
        TrieNode node = record.Hash == ValueKeccak.Zero
            ? new TrieNode(NodeType.Unknown, record.Rlp.ToArray())
            : new TrieNode(NodeType.Unknown, new Hash256(record.Hash), record.Rlp);
        // The node holds its RLP, so resolving it loads nothing by path.
        node.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        return node;
    }
}
