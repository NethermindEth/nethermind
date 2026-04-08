// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.StateComposition;

/// <summary>
/// Prunes old snapshot entries from the stateComposition database.
/// Subscribes to <see cref="IBlockTree.NewHeadBlock"/> and deletes entries
/// older than <see cref="IStateCompositionConfig.SnapshotBlocksToKeep"/>.
/// </summary>
public sealed class StateCompositionSnapshotPruner : IDisposable
{
    private readonly StateCompositionSnapshotStore _store;
    private readonly IBlockTree _blockTree;
    private readonly int _blocksToKeep;
    private readonly ILogger _logger;

    public StateCompositionSnapshotPruner(
        StateCompositionSnapshotStore store,
        IBlockTree blockTree,
        IStateCompositionConfig config,
        ILogManager logManager)
    {
        _store = store;
        _blockTree = blockTree;
        _blocksToKeep = config.SnapshotBlocksToKeep;
        _logger = logManager.GetClassLogger<StateCompositionSnapshotPruner>();

        if (_blocksToKeep > 0)
            _blockTree.NewHeadBlock += OnNewHeadBlock;
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        long deleteAt = e.Block.Number - _blocksToKeep;
        if (deleteAt <= 0) return;

        _ = Task.Run(() =>
        {
            try
            {
                _store.DeleteSnapshot(deleteAt);
            }
            catch (Exception ex)
            {
                if (_logger.IsDebug)
                    _logger.Debug($"StateComposition: failed to prune snapshot at block {deleteAt}: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        _blockTree.NewHeadBlock -= OnNewHeadBlock;
    }
}
