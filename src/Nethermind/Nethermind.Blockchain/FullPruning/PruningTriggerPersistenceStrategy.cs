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

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain.FullPruning;

/// <summary>
/// Forces to persist state trie to DB after next block of full pruning trigger <see cref="IPruningTrigger.Prune"/>.
/// </summary>
/// <remarks>
/// Used when both memory pruning and full pruning are enabled.
/// We need to store state trie to DB to be able to copy this trie into new database in full pruning.
/// </remarks>
public class PruningTriggerPersistenceStrategy : IPersistenceStrategy, IDisposable
{
    private readonly IFullPruningDb _fullPruningDb;
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;
    private int _inPruning = 0;
    private long? _minPersistedBlock = null;

    public PruningTriggerPersistenceStrategy(IFullPruningDb fullPruningDb, IBlockTree blockTree, ILogManager logManager)
    {
        _fullPruningDb = fullPruningDb;
        _blockTree = blockTree;
        _logger = logManager.GetClassLogger();
        _fullPruningDb.PruningFinished += OnPruningFinished;
        _fullPruningDb.PruningStarted += OnPruningStarted;
    }

    private void OnPruningStarted(object? sender, EventArgs e)
    {
        Interlocked.CompareExchange(ref _inPruning, 1, 0);
        _minPersistedBlock = null;
        _logger.Info("In Pruning, persisting all state changes");
    }
    
    private void OnPruningFinished(object? sender, EventArgs e)
    {
        _logger.Info("Out of Pruning, stop persisting all state changes");
        Interlocked.CompareExchange(ref _inPruning, 0, 1);
        _minPersistedBlock = null;
    }

    public bool ShouldPersist(long blockNumber)
    {
        bool inPruning = _inPruning != 0;
        if (inPruning)
        {
            _minPersistedBlock ??= blockNumber;
            if (blockNumber > _minPersistedBlock + Reorganization.MaxDepth)
            {
                _blockTree.BestPersistedState = blockNumber - Reorganization.MaxDepth;
            }
            else
            {
                _logger.Info($"Persisting state changes for {blockNumber}, from {_minPersistedBlock}");
            }
        }

        return inPruning;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _fullPruningDb.PruningStarted -= OnPruningStarted;
        _fullPruningDb.PruningFinished -= OnPruningFinished;
    }
}
