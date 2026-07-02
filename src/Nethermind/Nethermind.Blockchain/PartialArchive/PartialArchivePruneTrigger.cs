// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Blockchain.PartialArchive;

/// <summary>
/// Schedules partial archive window pruning: every <see cref="ISyncConfig.PartialArchivePruneInterval"/>
/// blocks it asks the <see cref="PartialArchiveNodeTracker"/> to delete state versions older than
/// <see cref="ISyncConfig.PartialArchiveRange"/> blocks behind the head, and publishes the
/// resulting retention floor to the state availability boundary.
/// </summary>
public sealed class PartialArchivePruneTrigger : IDisposable
{
    private readonly PartialArchiveNodeTracker _tracker;
    private readonly IBlockTree _blockTree;
    private readonly IStateBoundaryWriter? _stateBoundaryWriter;
    private readonly ulong _range;
    private readonly ulong _interval;
    private ulong _lastRequestHead;

    public PartialArchivePruneTrigger(
        PartialArchiveNodeTracker tracker,
        IBlockTree blockTree,
        ISyncConfig syncConfig,
        IStateBoundaryWriter? stateBoundaryWriter,
        ILogManager logManager)
    {
        _tracker = tracker;
        _blockTree = blockTree;
        _stateBoundaryWriter = stateBoundaryWriter;
        _range = Math.Max(1, syncConfig.PartialArchiveRange);
        _interval = Math.Max(1, syncConfig.PartialArchivePruneInterval);

        _blockTree.NewHeadBlock += OnNewHeadBlock;
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        ulong head = e.Block.Number;

        ulong floor = _tracker.OldestRetainedBlock;
        if (floor > 0 && _stateBoundaryWriter is not null)
        {
            // Monotonic no-op when unchanged; keeps eth_capabilities in sync with actual pruning.
            _stateBoundaryWriter.OldestStateBlock = floor;
        }

        if (head <= _range || head < _lastRequestHead + _interval) return;

        if (_tracker.RequestPrune(head - _range))
        {
            _lastRequestHead = head;
        }
    }

    public void Dispose() => _blockTree.NewHeadBlock -= OnNewHeadBlock;
}
