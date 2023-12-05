// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db.ByPathState;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain.ByPathState;
public class ByPathConstantPersistenceStrategy : IByPathPersistenceStrategy
{
    private readonly IByPathStateDb? _pathStateDb;
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;
    private readonly int _minInMemBlocks;
    private readonly int _persistInterval;
    private SortedDictionary<long, BlockHeader> _finalizedBlocks;

    public ByPathConstantPersistenceStrategy(IByPathStateDb? pathStateDb, IBlockTree blockTree, int minInMemBlocks, int persistInterval, ILogManager logManager)
    {
        _pathStateDb = pathStateDb;
        _blockTree = blockTree;
        _minInMemBlocks = minInMemBlocks;
        _persistInterval = persistInterval;
        _logger = logManager.GetClassLogger();
        _finalizedBlocks = new SortedDictionary<long, BlockHeader>();
    }

    public void FinalizationManager_BlocksFinalized(object? sender, FinalizeEventArgs e)
    {
        //TODO - when remove?
        foreach (BlockHeader b in e.FinalizedBlocks)
        {
            _finalizedBlocks[b.Number] = b;
        }
    }

    public (long blockNumber, Hash256 stateRoot)? GetBlockToPersist(long currentBlockNumber, Hash256 currentStateRoot)
    {
        //Cleaning of db data not finished
        if (_pathStateDb is not null && !(_pathStateDb.CanAccessByPath(Db.StateColumns.State) && _pathStateDb.CanAccessByPath(Db.StateColumns.Storage)))
            return null;

        long distanceToPersisted = currentBlockNumber - ((_blockTree.BestPersistedState ?? 0) + _minInMemBlocks);

        if (distanceToPersisted > 0 && distanceToPersisted % _persistInterval == 0)
        {
            long targetBlockNumber = currentBlockNumber - _minInMemBlocks;

            foreach (KeyValuePair<long, BlockHeader> block in _finalizedBlocks.Reverse())
            {
                if (block.Value.Number <= targetBlockNumber)
                    return (block.Value.Number, block.Value.StateRoot);
            }
        }
        return null;
    }
}
