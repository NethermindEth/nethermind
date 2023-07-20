// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin;

public class ProcessedTransactionsDbCleaner : IDisposable
{
    private readonly IManualBlockFinalizationManager _finalizationManager;
    private readonly IDb _processedTxsDb;
    private readonly ILogger _logger;
    private long _lastFinalizedBlock = 0;

    public ProcessedTransactionsDbCleaner(IManualBlockFinalizationManager finalizationManager, IDb processedTxsDb, ILogManager logManager)
    {
        _finalizationManager = finalizationManager ?? throw new ArgumentNullException(nameof(finalizationManager));
        _processedTxsDb = processedTxsDb ?? throw new ArgumentNullException(nameof(processedTxsDb));
        _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

        _finalizationManager.BlocksFinalized += OnBlocksFinalized;
    }

    private void OnBlocksFinalized(object? sender, FinalizeEventArgs e)
    {
        if (e.FinalizedBlocks.Count > 0 && e.FinalizedBlocks[0].Number > _lastFinalizedBlock)
        {
            Task.Run(() => CleanProcessedTransactionsDb(e.FinalizedBlocks[0].Number));
        }
    }

    private void CleanProcessedTransactionsDb(long newlyFinalizedBlockNumber)
    {
        try
        {
            List<long> blocksInDb = new();
            foreach (byte[] key in _processedTxsDb.GetAllKeys())
            {
                blocksInDb.Add(key.ToLongFromBigEndianByteArrayWithoutLeadingZeros());
            }

            foreach (long blockNumber in blocksInDb)
            {
                if (newlyFinalizedBlockNumber >= blockNumber)
                {
                    _processedTxsDb.Delete(blockNumber);
                }
            }

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
