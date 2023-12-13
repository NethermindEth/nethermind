// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.TraceStore;

/// <summary>
/// Prunes tracing history
/// </summary>
public class TraceStorePruner : IDisposable
{
    private readonly IBlockTree _blockTree;
    private readonly IDb _db;
    private readonly int _blockToKeep;
    private readonly ILogger _logger;

    public TraceStorePruner(IBlockTree blockTree, IDb db, int blockToKeep, ILogManager logManager)
    {
        _blockTree = blockTree;
        _db = db;
        _blockToKeep = blockToKeep;
        _logger = logManager.GetClassLogger<TraceStorePruner>();
        _blockTree.BlockAddedToMain += OnBlockAddedToMain;
        if (_logger.IsDebug) _logger.Debug($"TraceStore pruning is enabled, keeping last {blockToKeep} blocks.");
    }

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        Task.Run((() =>
        {
            long levelToDelete = e.Block.Number - _blockToKeep;
            if (levelToDelete > 0)
            {
                ChainLevelInfo? level = _blockTree.FindLevel(levelToDelete);
                if (level is not null)
                {
                    for (int i = 0; i < level.BlockInfos.Length; i++)
                    {
                        BlockInfo blockInfo = level.BlockInfos[i];
                        if (_logger.IsTrace) _logger.Trace($"Removing traces from TraceStore on level {levelToDelete} for block {blockInfo}.");
                        _db.Delete(blockInfo.BlockHash);
                    }
                }
            }
        }));
    }

    public void Dispose()
    {
        _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
    }
}
