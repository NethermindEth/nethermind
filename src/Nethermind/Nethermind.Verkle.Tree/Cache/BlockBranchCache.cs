// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.VerkleDb;
namespace Nethermind.Verkle.Tree.Cache;

public readonly struct StateInfo(SortedVerkleMemoryDb? stateDiff, Hash256 stateRoot, long blockNumber)
{
    public SortedVerkleMemoryDb? StateDiff { get; } = stateDiff;
    public Hash256 StateRoot { get; } = stateRoot;
    public long BlockNumber { get; } = blockNumber;
}


public sealed class BlockBranchNode(SortedVerkleMemoryDb? stateDiff, Hash256 stateRoot, long blockNumber)
{
    public StateInfo Data { get; set; } = new(stateDiff, stateRoot, blockNumber);
    public BlockBranchNode? ParentNode { get; set; }
}

public class BlockBranchCache(int cacheSize)
{
    private readonly SpanDictionary<byte, BlockBranchNode> _stateRootToNodeMapping = new(Bytes.SpanEqualityComparer);
    private BlockBranchNode? _lastNode;
    private int _version;
    public bool IsInitialized => _lastNode is not null;

    /// <summary>
    ///     This is basically used to have a reference to the state that is persisted. The reason for doing this is -
    ///     when the first two blocks inserted in the cache has the same block number, this creates a situation where we
    ///     don't have just one _lastNode and we might need to maintain multiple independent branches and that would be hard.
    ///     Instead, maintain at least one block that is finalized (weakly), so that it can act as a pivot from where we can
    ///     create multiple branches without the need to keeping track of independent branches.
    /// </summary>
    /// <param name="blockNumber"></param>
    /// <param name="stateRoot"></param>
    /// <returns></returns>
    public void InitCache(long blockNumber, Hash256 stateRoot)
    {
        if (IsInitialized) throw new InvalidOperationException("This cache is already initialized");
        var node = new BlockBranchNode(null, stateRoot, blockNumber);
        _stateRootToNodeMapping[stateRoot.Bytes] = _lastNode = node;
        _version++;
    }

    private bool AddNode(long blockNumber, Hash256 stateRoot, SortedVerkleMemoryDb data, Hash256 parentRoot,
        [MaybeNullWhen(false)] out BlockBranchNode outNode)
    {
        if (!IsInitialized) throw new InvalidOperationException("Cache needs to be initialized first");
        var node = new BlockBranchNode(data, stateRoot, blockNumber);
        BlockBranchNode? parentNode = _stateRootToNodeMapping[parentRoot.Bytes];
        node.ParentNode = parentNode;
        outNode = _stateRootToNodeMapping[stateRoot.Bytes] = node;
        _version++;
        return true;
    }

    public bool EnqueueAndReplaceIfFull(long blockNumber, Hash256 stateRoot, SortedVerkleMemoryDb data,
        Hash256 parentRoot, [MaybeNullWhen(false)] out StateInfo node)
    {
        if (!AddNode(blockNumber, stateRoot, data, parentRoot, out BlockBranchNode insertedNode))
            throw new InvalidOperationException("Failed to add node.");

        if (_lastNode is not null && blockNumber - _lastNode.Data.BlockNumber > cacheSize)
        {
            BlockBranchNode? currentNode = insertedNode;
            while (currentNode.ParentNode?.ParentNode is not null) currentNode = currentNode.ParentNode;
            _lastNode = currentNode;
            node = currentNode.Data;

            // Remove the least recently used node to maintain cache size
            _stateRootToNodeMapping.Remove(currentNode.ParentNode!.Data.StateRoot.Bytes);
            currentNode.ParentNode = null;
            currentNode.Data = new StateInfo(null, node.StateRoot, node.BlockNumber);
            return true;
        }

        node = default;
        return false;
    }

    public bool GetStateRootNode(Hash256 stateRoot, [MaybeNullWhen(false)] out BlockBranchNode node)
    {
        if (_lastNode is null || stateRoot == _lastNode.Data.StateRoot)
        {
            node = default;
            return false;
        }

        return _stateRootToNodeMapping.TryGetValue(stateRoot.Bytes, out node);
    }

    public byte[]? GetLeaf(byte[] key, Hash256 stateRoot)
    {
        foreach (BlockBranchNode? node in GetEnumerable(stateRoot))
        {
            if (node.Data.StateDiff?.LeafTable.TryGetValue(key, out var value) == true)
                return value;
        }
        return null;
    }

    public bool HasLeaf(byte[] key, Hash256 stateRoot)
    {
        foreach (BlockBranchNode? node in GetEnumerable(stateRoot))
        {
            if (node.Data.StateDiff?.LeafTable.ContainsKey(key) == true)
                return true;
        }
        return false;
    }

    public InternalNode? GetInternalNode(byte[] key, Hash256 stateRoot)
    {
        foreach (BlockBranchNode? node in GetEnumerable(stateRoot))
        {
            if (node.Data.StateDiff?.InternalTable.TryGetValue(key, out var internalNode) == true)
                return internalNode.Clone();
        }
        return null;
    }

    // Use IEnumerable for better abstraction and compatibility
    public IEnumerable<BlockBranchNode> GetEnumerable(Hash256 stateRoot)
    {
        if (!GetStateRootNode(stateRoot, out var node)) yield break;

        // the reason we use node.ParentNode != null is that the lastNode we have is just a placeholder
        // for the state that is already persisted in the database
        while (node.ParentNode != null)
        {
            yield return node;
            node = node.ParentNode;
        }
    }
}
