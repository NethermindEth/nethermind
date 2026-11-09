// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Runs an <see cref="INodeRecordVisitor{TNodeContext}"/> as an <see cref="ITreeVisitor{TNodeContext}"/>, encoding a record of each <see cref="TrieNode"/> it is handed.</summary>
/// <remarks>
/// It takes only the nodes a record can hold, as <see cref="NodeRecordCodec.TryDecode"/> decides: those of tries whose embedded nodes are leaves,
/// as keccak-keyed state and storage tries are. An embedded node, which has no hash, gets a zero <see cref="NodeRecordRef.Hash"/>.
/// </remarks>
[SkipLocalsInit]
public sealed class NodeRecordVisitorAdapter<TNodeContext>(INodeRecordVisitor<TNodeContext> visitor) : ITreeVisitor<TNodeContext>
    where TNodeContext : struct, INodeContext<TNodeContext>
{
    /// <summary>Fits any branch, extension, account or storage slot record.</summary>
    private const int StackRecordLength = 1024;

    /// <inheritdoc/>
    public bool IsFullDbScan => visitor.IsFullDbScan;

    /// <inheritdoc/>
    public bool ExpectAccounts => visitor.ExpectAccounts;

    /// <inheritdoc/>
    public ReadFlags ExtraReadFlag => visitor.ExtraReadFlag;

    /// <inheritdoc/>
    public bool ShouldVisit(in TNodeContext nodeContext, in ValueHash256 nextNode) => visitor.ShouldVisit(nodeContext, nextNode);

    /// <inheritdoc/>
    public void VisitTree(in TNodeContext nodeContext, in ValueHash256 rootHash) => visitor.VisitTree(nodeContext, rootHash);

    /// <inheritdoc/>
    public void VisitMissingNode(in TNodeContext nodeContext, in ValueHash256 nodeHash) => visitor.VisitMissingNode(nodeContext, nodeHash);

    /// <inheritdoc/>
    public void VisitBranch(in TNodeContext nodeContext, TrieNode node)
    {
        Span<byte> buffer = stackalloc byte[StackRecordLength];
        visitor.VisitBranch(nodeContext, NodeRecordVisitorAdapter.ToRecord(node, buffer));
    }

    /// <inheritdoc/>
    public void VisitExtension(in TNodeContext nodeContext, TrieNode node)
    {
        Span<byte> buffer = stackalloc byte[StackRecordLength];
        visitor.VisitExtension(nodeContext, NodeRecordVisitorAdapter.ToRecord(node, buffer));
    }

    /// <inheritdoc/>
    public void VisitLeaf(in TNodeContext nodeContext, TrieNode node)
    {
        Span<byte> buffer = stackalloc byte[StackRecordLength];
        visitor.VisitLeaf(nodeContext, NodeRecordVisitorAdapter.ToRecord(node, buffer));
    }

    /// <inheritdoc/>
    public void VisitAccount(in TNodeContext nodeContext, TrieNode node, in AccountStruct account)
    {
        Span<byte> buffer = stackalloc byte[StackRecordLength];
        visitor.VisitAccount(nodeContext, NodeRecordVisitorAdapter.ToRecord(node, buffer), account);
    }
}

/// <summary>The part of <see cref="NodeRecordVisitorAdapter{TNodeContext}"/> that does not depend on the node context, so it is compiled once.</summary>
internal static class NodeRecordVisitorAdapter
{
    /// <param name="buffer">Holds the record when it fits; a longer one is allocated.</param>
    /// <exception cref="TrieException">The node is not one a record can hold.</exception>
    public static NodeRecordRef ToRecord(TrieNode node, Span<byte> buffer)
    {
        ReadOnlySpan<byte> rlp = node.FullRlp.AsSpan();
        if (!NodeRecordCodec.TryDecode(rlp, out NodeRecordKind kind, out ushort hashMask))
            throw new TrieException("A visited trie node cannot be held as a node record.");

        int length = NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length);
        Span<byte> record = length <= buffer.Length ? buffer[..length] : new byte[length];
        NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, node.Keccak is { } hash ? hash.ValueHash256 : ValueKeccak.Zero, 0, record);
        return new NodeRecordRef(record);
    }
}
