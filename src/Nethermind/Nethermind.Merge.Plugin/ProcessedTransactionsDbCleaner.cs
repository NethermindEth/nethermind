// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin;

public class ProcessedTransactionsDbCleaner : IDisposable
{
    private readonly IBlockTree _blockTree;
    private readonly IDb _processedTxsDb;
    private readonly ILogger _logger;
    private ulong _lastFinalizedBlock = 0;
    public Task CleaningTask { get; private set; } = Task.CompletedTask;

    public ProcessedTransactionsDbCleaner(IBlockTree blockTree, IDbProvider dbProvider, ILogManager logManager, ITxPoolConfig txPoolConfig)
    {
        ArgumentNullException.ThrowIfNull(dbProvider);
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _processedTxsDb = dbProvider.BlobTransactionsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs);
        _logger = logManager?.GetClassLogger<ProcessedTransactionsDbCleaner>() ?? throw new ArgumentNullException(nameof(logManager));

        // Only blob-tx reorg support persists processed txs, so there is nothing to clean otherwise.
        if (txPoolConfig.BlobsSupport.SupportsReorgs())
            _blockTree.BlocksFinalized += OnBlocksFinalized;
    }

    private void OnBlocksFinalized(object? sender, FinalizeEventArgs e)
    {
        if (e.FinalizedBlock.Number > _lastFinalizedBlock && CleaningTask.IsCompleted)
        {
            CleaningTask = Task.Run(() => CleanProcessedTransactionsDb(e.FinalizedBlock.Number));
        }
    }

    private void CleanProcessedTransactionsDb(ulong newlyFinalizedBlockNumber)
    {
        try
        {
            using (IWriteBatch writeBatch = _processedTxsDb.StartWriteBatch())
            {
                foreach (byte[] key in _processedTxsDb.GetAllKeys())
                {
                    ulong blockNumber = key.ToULongFromBigEndianByteArrayWithoutLeadingZeros();
                    if (newlyFinalizedBlockNumber >= blockNumber)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Cleaning processed blob txs from block {blockNumber}");
                        writeBatch.Delete(blockNumber);
                    }
                }
            }

            if (_logger.IsDebug) _logger.Debug($"Cleaned processed blob txs from block {_lastFinalizedBlock} to block {newlyFinalizedBlockNumber}");

            _processedTxsDb.Compact();

            if (_logger.IsDebug) _logger.Debug($"Blob transactions database columns have been compacted");

            _lastFinalizedBlock = newlyFinalizedBlockNumber;
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Couldn't correctly clean db with processed transactions. Newly finalized block {newlyFinalizedBlockNumber}, last finalized block: {_lastFinalizedBlock}", exception);
        }
    }

    public void Dispose() => _blockTree.BlocksFinalized -= OnBlocksFinalized;
}
