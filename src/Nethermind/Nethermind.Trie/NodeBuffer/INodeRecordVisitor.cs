// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>A tree visitor that reads each node in place, as a <see cref="NodeRecordRef"/>, rather than as a <see cref="TrieNode"/>.</summary>
/// <remarks>
/// A walk over node records calls it as <see cref="ITreeVisitor{TNodeContext}"/> is called, so a visitor can implement both.
/// A node embedded in its parent has a zero <see cref="NodeRecordRef.Hash"/>.
/// </remarks>
public interface INodeRecordVisitor<TNodeContext>
    where TNodeContext : struct, INodeContext<TNodeContext>
{
    /// <inheritdoc cref="ITreeVisitor{TNodeContext}.IsFullDbScan"/>
    bool IsFullDbScan { get; }

    /// <inheritdoc cref="ITreeVisitor{TNodeContext}.IsRangeScan"/>
    bool IsRangeScan { get; }

    /// <inheritdoc cref="ITreeVisitor{TNodeContext}.ExpectAccounts"/>
    bool ExpectAccounts => true;

    /// <inheritdoc cref="ITreeVisitor{TNodeContext}.ExtraReadFlag"/>
    ReadFlags ExtraReadFlag => ReadFlags.None;

    bool ShouldVisit(in TNodeContext nodeContext, in ValueHash256 nextNode);

    void VisitTree(in TNodeContext nodeContext, in ValueHash256 rootHash);

    void VisitMissingNode(in TNodeContext nodeContext, in ValueHash256 nodeHash);

    void VisitBranch(in TNodeContext nodeContext, NodeRecordRef node);

    void VisitExtension(in TNodeContext nodeContext, NodeRecordRef node);

    void VisitLeaf(in TNodeContext nodeContext, NodeRecordRef node);

    /// <inheritdoc cref="ITreeVisitor{TNodeContext}.VisitAccount"/>
    void VisitAccount(in TNodeContext nodeContext, NodeRecordRef node, in AccountStruct account);
}
