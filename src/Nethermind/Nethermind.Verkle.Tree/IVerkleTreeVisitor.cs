// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree;

public interface IVerkleTreeVisitor<TNodeContext>
    where TNodeContext : struct, INodeContext<TNodeContext>
{

    /// <summary>
    /// Specify that this is a full table scan and should optimize for it.
    /// </summary>
    public bool IsFullDbScan { get; }

    /// <summary>
    /// Used by snap sync, specify that a range of nodes in increasing order will be traversed. This turn on
    /// some optimization for this specific scenario.
    /// </summary>
    public bool IsRangeScan => IsFullDbScan;

    /// <summary>
    /// Specify that the account will be decoded and code and storage will get traversed.
    /// </summary>
    public bool ExpectAccounts => true;

    /// <summary>
    /// Extra read flags for passing to triestore. Used to optimize snap sync's gettrie so that it won't effect
    /// block processing.
    /// </summary>
    ReadFlags ExtraReadFlag => ReadFlags.None;

    bool ShouldVisit(byte[] nextNode);

    void VisitTree(in TNodeContext nodeContext, in ValueHash256 rootHash);

    void VisitMissingNode(in TNodeContext nodeContext, byte[] nodeKey);

    void VisitBranchNode(in TNodeContext nodeContext, InternalNode node);

    void VisitStemNode(in TNodeContext nodeContext, InternalNode node);

    void VisitLeafNode(in TNodeContext nodeContext, ReadOnlySpan<byte> nodeKey, byte[]? nodeValue);
}
