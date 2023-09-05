// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db.ByPathState;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain.ByPathState;
public class ByPathConstantPersistenceStrategy : IPersistenceStrategy
{
    private readonly IByPathStateDb? _pathStateDb;
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;
    private readonly int _minInMemBlocks;
    private readonly int _persistInterval;

    public ByPathConstantPersistenceStrategy(IByPathStateDb? pathStateDb, IBlockTree blockTree, int minInMemBlocks, int persistInterval, ILogManager logManager)
    {
        _pathStateDb = pathStateDb;
        _blockTree = blockTree;
        _minInMemBlocks = minInMemBlocks;
        _persistInterval = persistInterval;
        _logger = logManager.GetClassLogger();
    }

    public bool ShouldPersist(long blockNumber)
    {
        return false;
    }

    public bool ShouldPersist(long currentBlockNumber, out long targetBlockNumber)
    {
        targetBlockNumber = -1;
        //Cleaning of db data not finished
        if (_pathStateDb is not null && !(_pathStateDb.CanAccessByPath(Db.StateColumns.State) && _pathStateDb.CanAccessByPath(Db.StateColumns.Storage)))
            return false;

        long distanceToPersisted = currentBlockNumber - ((_blockTree.BestPersistedState ?? 0) + _minInMemBlocks);

        if (distanceToPersisted > 0 && distanceToPersisted % _persistInterval == 0)
        {
            targetBlockNumber = currentBlockNumber - _minInMemBlocks;
            return true;
        }
        return false;
    }
}
