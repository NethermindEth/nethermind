//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

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
