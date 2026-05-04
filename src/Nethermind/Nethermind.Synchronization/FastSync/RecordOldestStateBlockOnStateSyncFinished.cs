// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;

namespace Nethermind.Synchronization.FastSync;

/// <summary>
/// On state sync (fast/snap) completion records the pivot block as the oldest state available
/// in <see cref="IBlockTree.OldestStateBlock"/> so eth_capabilities can report a correct floor.
/// </summary>
public class RecordOldestStateBlockOnStateSyncFinished
{
    private readonly IBlockTree _blockTree;
    private readonly ITreeSync _treeSync;

    public RecordOldestStateBlockOnStateSyncFinished(IBlockTree blockTree, ITreeSync treeSync)
    {
        _blockTree = blockTree;
        _treeSync = treeSync;
        _treeSync.SyncCompleted += OnSyncCompleted;
    }

    private void OnSyncCompleted(object? sender, ITreeSync.SyncCompletedEventArgs evt)
    {
        _treeSync.SyncCompleted -= OnSyncCompleted;
        _blockTree.OldestStateBlock = evt.Pivot.Number;
    }
}
