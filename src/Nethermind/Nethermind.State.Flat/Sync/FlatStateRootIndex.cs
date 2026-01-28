// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// Tracks mapping from state root hash to StateId for serving snap sync requests.
/// Similar to <see cref="Nethermind.Blockchain.Utils.LastNStateRootTracker"/> but stores StateId for lookup.
/// </summary>
public class FlatStateRootIndex(IBlockTree blockTree, int lastN) : IFlatStateRootIndex, IDisposable
{
    private Hash256? _lastQueuedStateRoot;
    private Queue<Hash256> _stateRootQueue = new();
    private NonBlocking.ConcurrentDictionary<Hash256AsKey, StateId> _availableStateRoots = new();

    public void Initialize()
    {
        blockTree.BlockAddedToMain += BlockTreeOnNewHeadBlock;
        if (blockTree.Head is not null) ResetAvailableStateRoots(blockTree.Head.Header, true);
    }

    private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e) =>
        ResetAvailableStateRoots(e.Block.Header, false);

    private void ResetAvailableStateRoots(BlockHeader? newHead, bool resetQueue)
    {
        if (newHead?.StateRoot is null) return;
        if (_availableStateRoots.ContainsKey(newHead.StateRoot)) return;

        BlockHeader? parent = blockTree.FindParentHeader(newHead, BlockTreeLookupOptions.All);
        if (parent?.StateRoot is null) return;

        if (!resetQueue && _lastQueuedStateRoot == parent.StateRoot)
        {
            // Queue is intact - just add the new state root
            _availableStateRoots[newHead.StateRoot] = new StateId(newHead);
            while (_stateRootQueue.Count >= lastN && _stateRootQueue.TryDequeue(out Hash256? oldStateRoot))
            {
                if (oldStateRoot is not null)
                    _availableStateRoots.TryRemove(oldStateRoot, out _);
            }
            _stateRootQueue.Enqueue(newHead.StateRoot);
            _lastQueuedStateRoot = newHead.StateRoot;
            return;
        }

        // Reset the queue and rebuild from scratch
        using ArrayPoolList<(Hash256 stateRoot, StateId stateId)> stateRoots = new(128);
        NonBlocking.ConcurrentDictionary<Hash256AsKey, StateId> newStateRootSet = new();
        newStateRootSet[newHead.StateRoot] = new StateId(newHead);
        stateRoots.Add((newHead.StateRoot, new StateId(newHead)));

        BlockHeader? current = parent;
        while (current?.StateRoot is not null && stateRoots.Count < lastN)
        {
            StateId stateId = new(current);
            newStateRootSet[current.StateRoot] = stateId;
            stateRoots.Add((current.StateRoot, stateId));
            current = blockTree.FindParentHeader(current, BlockTreeLookupOptions.All);
        }

        _availableStateRoots = newStateRootSet;
        stateRoots.Reverse();
        _stateRootQueue = new Queue<Hash256>(stateRoots.Select(x => x.stateRoot));
        _lastQueuedStateRoot = newHead.StateRoot;
    }

    public bool HasStateRoot(Hash256 stateRoot) => _availableStateRoots.ContainsKey(stateRoot);

    public bool TryGetStateId(Hash256 stateRoot, out StateId stateId) =>
        _availableStateRoots.TryGetValue(stateRoot, out stateId);

    public void Dispose() => blockTree.BlockAddedToMain -= BlockTreeOnNewHeadBlock;
}
