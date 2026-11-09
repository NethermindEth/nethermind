// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>A tree visitor that reads each node in place, as a <see cref="NodeRecordRef"/>, rather than as a <see cref="TrieNode"/>.</summary>
/// <remarks>
/// A walk over node records calls it as <see cref="ITreeVisitor{TNodeContext}"/> is called.
/// A node embedded in its parent has a zero <see cref="NodeRecordRef.Hash"/>.
/// </remarks>
public interface INodeRecordVisitor<TNodeContext>
    where TNodeContext : struct, INodeContext<TNodeContext>
{
    /// <inheritdoc cref="ITreeVisitor{TNodeContext}.IsFullDbScan"/>
    bool IsFullDbScan { get; }

    /// <inheritdoc cref="ITreeVisitor{TNodeContext}.ExpectAccounts"/>
    bool ExpectAccounts => true;

    /// <inheritdoc cref="ITreeVisitor{TNodeContext}.ExtraReadFlag"/>
    ReadFlags ExtraReadFlag => ReadFlags.None;

    /// <summary>Whether to visit the node <paramref name="nextNode"/> names at <paramref name="nodeContext"/>, or the storage trie of that root.</summary>
    /// <param name="nextNode">The node's hash; zero for a node embedded in its parent.</param>
    bool ShouldVisit(in TNodeContext nodeContext, in ValueHash256 nextNode);

    /// <summary>Called with the root hash of the walked state trie, before any of its nodes.</summary>
    void VisitTree(in TNodeContext nodeContext, in ValueHash256 rootHash);

    /// <summary>Called instead of visiting a node the walk cannot load.</summary>
    void VisitMissingNode(in TNodeContext nodeContext, in ValueHash256 nodeHash);

    /// <summary>Visits a branch, before its children.</summary>
    void VisitBranch(in TNodeContext nodeContext, NodeRecordRef node);

    /// <summary>Visits an extension, before its child.</summary>
    void VisitExtension(in TNodeContext nodeContext, NodeRecordRef node);

    /// <summary>Visits a leaf; a state trie leaf is then visited as an account when <see cref="ExpectAccounts"/> is set.</summary>
    void VisitLeaf(in TNodeContext nodeContext, NodeRecordRef node);

    /// <inheritdoc cref="ITreeVisitor{TNodeContext}.VisitAccount"/>
    void VisitAccount(in TNodeContext nodeContext, NodeRecordRef node, in AccountStruct account);
}
