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

public struct StateInfo(SortedVerkleMemoryDb? stateDiff, Hash256 stateRoot, long blockNumber)
{
    public readonly SortedVerkleMemoryDb? StateDiff = stateDiff;
    public readonly Hash256 StateRoot = stateRoot;
    public readonly long BlockNumber = blockNumber;
}

public sealed class BlockBranchNode(SortedVerkleMemoryDb? stateDiff, Hash256 stateRoot, long blockNumber)
{
    public StateInfo Data = new(stateDiff, stateRoot, blockNumber);
    public BlockBranchNode? ParentNode;
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
    ///     dont have just one _lastNode and we might need to maintain multiple independent branches and that would be hard.
    ///     Instead maintain at least one block that is finalized (weakly), so that it can act as a pivot from where we can
    ///     create multiple branches without the need to keeping track of independent branches.
    /// </summary>
    /// <param name="blockNumber"></param>
    /// <param name="stateRoot"></param>
    /// <returns></returns>
    public void InitCache(long blockNumber, Hash256 stateRoot)
    {
        if (IsInitialized) throw new Exception("This cache is already initialized");
        var node = new BlockBranchNode(null, stateRoot, blockNumber);
        _stateRootToNodeMapping[stateRoot.Bytes] = _lastNode = node;
        _version++;
    }

    private bool AddNode(long blockNumber, Hash256 stateRoot, SortedVerkleMemoryDb data, Hash256 parentRoot,
        [MaybeNullWhen(false)] out BlockBranchNode outNode)
    {
        if (!IsInitialized) throw new Exception("Cache needs to be initialized first");
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
            throw new Exception("This is a error");

        if (_lastNode is not null)
            if (blockNumber - _lastNode.Data.BlockNumber > cacheSize)
            {
                BlockBranchNode? currentNode = insertedNode;
                while (currentNode.ParentNode!.ParentNode is not null) currentNode = currentNode.ParentNode;
                _lastNode = currentNode;
                node = currentNode.Data;
                _stateRootToNodeMapping.Remove(currentNode.ParentNode.Data.StateRoot.Bytes);
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
        BranchEnumerator diffs = GetEnumerator(stateRoot);
        while (diffs.MoveNext())
            if (diffs.Current.Data.StateDiff!.LeafTable.TryGetValue(key.ToArray(), out var node))
                return node;
        return null;
    }

    public InternalNode? GetInternalNode(byte[] key, Hash256 stateRoot)
    {
        BranchEnumerator diffs = GetEnumerator(stateRoot);
        while (diffs.MoveNext())
            if (diffs.Current.Data.StateDiff!.InternalTable.TryGetValue(key, out InternalNode? node))
                return node!.Clone();
        return null;
    }

    public BranchEnumerator GetEnumerator(Hash256 stateRoot)
    {
        return new BranchEnumerator(this, stateRoot);
    }

    public struct BranchEnumerator : IEnumerator
    {
        private readonly Hash256 _startingStateRoot;
        private BlockBranchNode? _node;
        private readonly BlockBranchCache _cache;
        private int _version;
        private BlockBranchNode? _currentElement;

        public BranchEnumerator(BlockBranchCache cache, Hash256 startingStateRoot)
        {
            _startingStateRoot = startingStateRoot;
            _cache = cache;
            _version = cache._version;
            _cache.GetStateRootNode(_startingStateRoot, out _node);
            _currentElement = default;
        }

        public bool MoveNext()
        {
            if (_version != _cache._version) throw new Exception("Should be same version");
            if (_node?.ParentNode == null) return false;
            _currentElement = _node;
            _node = _node.ParentNode;
            return true;
        }

        public void Reset()
        {
            _version = _cache._version;
            _currentElement = default;
            _cache.GetStateRootNode(_startingStateRoot, out _node);
        }

        public BlockBranchNode Current => _currentElement!;
        object IEnumerator.Current => _currentElement!;
    }
}
