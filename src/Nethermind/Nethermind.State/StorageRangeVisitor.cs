// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State;

/// <summary>
/// Collects the storage slots of one account in hashed-key order, starting at a hashed key and stopping at a slot limit.
/// </summary>
/// <remarks>
/// Only the trie path to the account and the storage subtrees at or after the start key are read. When more slots follow
/// the collected ones, <see cref="NextKey"/> is the hashed key of the first of them, as in geth's <c>debug_storageRangeAt</c>.
/// </remarks>
public sealed class StorageRangeVisitor(Address address, in ValueHash256 startKey, int limit) : INodeRecordVisitor<TreePathContextWithStorage>
{
    private readonly TreePath _accountPath = new(ValueKeccak.Compute(address.Bytes), 64);
    private readonly TreePath _startKey = new(startKey, 64);
    private readonly List<(ValueHash256 Key, ValueHash256 Value)> _slots = [];
    private bool _isComplete;

    /// <summary>Whether the account exists and has a non-empty storage trie.</summary>
    public bool HasStorage { get; private set; }

    /// <summary>The collected slots as hashed key and 32-byte value, in hashed-key order.</summary>
    public IReadOnlyList<(ValueHash256 Key, ValueHash256 Value)> Slots => _slots;

    /// <summary>The hashed key of the first slot after the collected ones, or <see langword="null"/> when none follows.</summary>
    public ValueHash256? NextKey { get; private set; }

    public bool IsFullDbScan => false;

    public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) =>
        !_isComplete && (nodeContext.Storage is null
            ? _accountPath.CompareToTruncated(nodeContext.Path, nodeContext.Path.Length) == 0
            : _startKey.CompareToTruncated(nodeContext.Path, nodeContext.Path.Length) <= 0);

    public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash)
    {
    }

    public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) =>
        throw new MissingTrieNodeException($"Missing trie node {nodeHash} at {nodeContext.Path}", nodeContext.Storage, nodeContext.Path, new Hash256(nodeHash));

    /// <inheritdoc/>
    public void VisitBranch(in TreePathContextWithStorage nodeContext, NodeRecordRef node)
    {
    }

    /// <inheritdoc/>
    public void VisitExtension(in TreePathContextWithStorage nodeContext, NodeRecordRef node)
    {
    }

    /// <inheritdoc/>
    public void VisitLeaf(in TreePathContextWithStorage nodeContext, NodeRecordRef node)
    {
        if (nodeContext.Storage is null) return;

        Span<byte> leafKey = stackalloc byte[node.KeyLength(0)];
        node.CopyKey(0, leafKey);
        ValueHash256 key = nodeContext.Path.Append(leafKey).Path;
        if (key.CompareTo(_startKey.Path) < 0) return;

        if (_slots.Count >= limit)
        {
            NextKey = key;
            _isComplete = true;
            return;
        }

        RlpReader reader = new(node.LeafValue);
        ReadOnlySpan<byte> content = reader.DecodeByteArraySpan(RlpLimit.L32);
        ValueHash256 value = default;
        content.CopyTo(value.BytesAsSpan[(ValueHash256.MemorySize - content.Length)..]);
        _slots.Add((key, value));
    }

    /// <inheritdoc/>
    public void VisitAccount(in TreePathContextWithStorage nodeContext, NodeRecordRef node, in AccountStruct account)
    {
        if (nodeContext.Path.Path == _accountPath.Path) HasStorage = account.HasStorage;
    }
}
