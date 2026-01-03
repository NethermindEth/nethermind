// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin;

public class ProcessedTransactionsDbCleaner : IDisposable
{
    private readonly IBlockFinalizationManager _finalizationManager;
    private readonly IDb _processedTxsDb;
    private readonly ILogger _logger;
    private long _lastFinalizedBlock = 0;
    public Task CleaningTask { get; private set; } = Task.CompletedTask;

    public ProcessedTransactionsDbCleaner(IBlockFinalizationManager finalizationManager, IDb processedTxsDb, ILogManager logManager)
    {
        _finalizationManager = finalizationManager ?? throw new ArgumentNullException(nameof(finalizationManager));
        _processedTxsDb = processedTxsDb ?? throw new ArgumentNullException(nameof(processedTxsDb));
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

        _finalizationManager.BlocksFinalized += OnBlocksFinalized;
    }

    private void OnBlocksFinalized(object? sender, FinalizeEventArgs e)
    {
        if (e.FinalizedBlocks.Count > 0 && CleaningTask.IsCompleted)
        {
            long finalizedBlockNumber = checked((long)e.FinalizedBlocks[0].Number);
            if (finalizedBlockNumber > _lastFinalizedBlock)
            {
                CleaningTask = Task.Run(() => CleanProcessedTransactionsDb(finalizedBlockNumber));
            }
        }
    }

    private void CleanProcessedTransactionsDb(long newlyFinalizedBlockNumber)
    {
        try
        {
            using (IWriteBatch writeBatch = _processedTxsDb.StartWriteBatch())
            {
                foreach (byte[] key in _processedTxsDb.GetAllKeys())
                {
                    long blockNumber = key.ToLongFromBigEndianByteArrayWithoutLeadingZeros();
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

    public void Dispose()
    {
        _finalizationManager.BlocksFinalized -= OnBlocksFinalized;
    }
}
