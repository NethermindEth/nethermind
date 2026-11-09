// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>A tree visitor that takes each node as its <see cref="NodeRecord"/> rather than as a <see cref="TrieNode"/>.</summary>
/// <remarks>
/// A walk over node records calls it as <see cref="ITreeVisitor{TNodeContext}"/> is called, so a visitor can implement both.
/// A record is readable only during the callback that takes it. A node embedded in its parent has a zero <see cref="NodeRecord.Hash"/>.
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

    bool ShouldVisit(in TNodeContext nodeContext, in ValueHash256 nextNode);

    void VisitTree(in TNodeContext nodeContext, in ValueHash256 rootHash);

    void VisitMissingNode(in TNodeContext nodeContext, in ValueHash256 nodeHash);

    void VisitBranch(in TNodeContext nodeContext, in NodeRecord node);

    void VisitExtension(in TNodeContext nodeContext, in NodeRecord node);

    void VisitLeaf(in TNodeContext nodeContext, in NodeRecord node);

    /// <inheritdoc cref="ITreeVisitor{TNodeContext}.VisitAccount"/>
    void VisitAccount(in TNodeContext nodeContext, in NodeRecord node, in AccountStruct account);
}
