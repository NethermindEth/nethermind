// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        if (_logger.IsDebug) _logger.Debug("In Full Pruning, persisting all state changes");
    }

    private void OnPruningFinished(object? sender, EventArgs e)
    {
        if (_logger.IsDebug) _logger.Debug("Out of Full Pruning, stop persisting all state changes");
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
                if (_logger.IsInfo) _logger.Info($"Persisting state changes for {blockNumber}, from {_minPersistedBlock}");
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

    public bool ShouldPersist(long currentBlockNumber, out long targetBlockNumber)
    {
        targetBlockNumber = currentBlockNumber;
        return ShouldPersist(currentBlockNumber);
    }
}
