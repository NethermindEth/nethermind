// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Utils;

// TODO: Move responsibility to IWorldStateManager? Could be, but if IWorldStateManager store more than 128 blocks
// of state, that would be out of spec for snap and it would fail hive test.
public class LastNStateRootTracker : ILastNStateRootTracker, IDisposable
{
    private IBlockTree _blockTree;
    private readonly int _lastN = 0;

    private Hash256? _lastQueuedStateRoot = null;
    private Queue<Hash256> _stateRootQueue = new Queue<Hash256>();
    private NonBlocking.ConcurrentDictionary<Hash256AsKey, int> _availableStateRoots = new();

    public LastNStateRootTracker(IBlockTree blockTree, int lastN)
    {
        _blockTree = blockTree;
        _lastN = lastN;

        _blockTree.BlockAddedToMain += BlockTreeOnNewHeadBlock;
        if (_blockTree.Head != null) ResetAvailableStateRoots(_blockTree.Head.Header, true);
    }

    private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        ResetAvailableStateRoots(e.Block.Header, false);
    }

    private void ResetAvailableStateRoots(BlockHeader? newHead, bool resetQueue)
    {
        if (_availableStateRoots.TryGetValue(newHead.StateRoot, out int num) && num > 0) return;

        BlockHeader? parent = _blockTree.FindParentHeader(newHead, BlockTreeLookupOptions.All);
        if (parent is null) return; // What? I don't wanna know...

        if (!resetQueue && _lastQueuedStateRoot == parent.StateRoot)
        {
            // Queue is intact.
            _availableStateRoots.AddOrUpdate(
                newHead.StateRoot,
                (_) => 1,
                (_, oldValue) => oldValue + 1);
            while (_stateRootQueue.Count >= _lastN && _stateRootQueue.TryDequeue(out Hash256 oldStateRoot))
            {
                int newNum = _availableStateRoots.AddOrUpdate(
                    oldStateRoot,
                    (_) => 0,
                    (_, oldValue) => oldValue - 1);
                if (newNum == 0) _availableStateRoots.Remove(oldStateRoot, out _);
            }
            _stateRootQueue.Enqueue(newHead.StateRoot);
            _lastQueuedStateRoot = newHead.StateRoot;
            return;
        }

        using ArrayPoolList<Hash256> stateRoots = new(128);
        NonBlocking.ConcurrentDictionary<Hash256AsKey, int> newStateRootSet = new();
        newStateRootSet.TryAdd(newHead.StateRoot, 1);
        stateRoots.Add(newHead.StateRoot);

        while (parent is not null && stateRoots.Count < _lastN)
        {
            newStateRootSet.AddOrUpdate(
                parent.StateRoot,
                (_) => 1,
                (_, oldValue) => oldValue + 1);
            stateRoots.Add(parent.StateRoot);
            parent = _blockTree.FindParentHeader(parent, BlockTreeLookupOptions.All);
        }

        _availableStateRoots = newStateRootSet;
        _stateRootQueue = new Queue<Hash256>(stateRoots.Reverse());
        _lastQueuedStateRoot = newHead.StateRoot;
    }

    public bool HasStateRoot(Hash256 stateRoot)
    {
        return _availableStateRoots.TryGetValue(stateRoot, out int num) && num > 0;
    }

    public void Dispose()
    {
        _blockTree.BlockAddedToMain -= BlockTreeOnNewHeadBlock;
    }
}
