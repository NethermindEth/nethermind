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
using Nethermind.Core;
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
    private readonly IPruningTrigger _pruningTrigger;
    private readonly IBlockTree _blockTree;
    private long? _shouldPersistBlockNumber = null;
    private readonly ILogger _logger;

    public PruningTriggerPersistenceStrategy(IPruningTrigger pruningTrigger, IBlockTree blockTree, ILogManager logManager)
    {
        _pruningTrigger = pruningTrigger;
        _blockTree = blockTree;
        _pruningTrigger.Prune += OnPrune;
        _logger = logManager.GetClassLogger();
    }

    private void OnPrune(object? sender, PruningEventArgs e)
    {
        _shouldPersistBlockNumber = (_blockTree.Head?.Number ?? 0) + 1;
    }

    public bool ShouldPersist(long blockNumber)
    {
        bool shouldPersist = blockNumber > _shouldPersistBlockNumber;
        if (shouldPersist)
        {
            if (_logger.IsInfo) _logger.Info($"Full Pruning Persisting state after block {_shouldPersistBlockNumber}.");
            _shouldPersistBlockNumber = null;
        }
        else if (_shouldPersistBlockNumber is not null)
        {
            if (_logger.IsInfo) _logger.Info($"Full Pruning Scheduled persisting state after block {_shouldPersistBlockNumber}.");
        }
        return shouldPersist;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _pruningTrigger.Prune -= OnPrune;
    }
}
