// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading;
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
    private readonly Lock _cleaningLock = new();
    private ulong _lastFinalizedBlock = 0;
    private ulong _pendingFinalizedBlock = 0;
    private bool _isCleaning;
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
        lock (_cleaningLock)
        {
            // A finalization arriving while a clean runs must be recorded rather than dropped: the running
            // cleaner re-reads this target, and cleaning to the highest one also covers everything below it.
            if (e.FinalizedBlock.Number <= _pendingFinalizedBlock) return;
            _pendingFinalizedBlock = e.FinalizedBlock.Number;

            if (_isCleaning) return;
            _isCleaning = true;
        }

        CleaningTask = Task.Run(RunCleaner);
    }

    private void RunCleaner()
    {
        try
        {
            while (true)
            {
                ulong target;
                lock (_cleaningLock)
                {
                    target = _pendingFinalizedBlock;
                    if (target <= _lastFinalizedBlock)
                    {
                        // Publishing idle has to be atomic with deciding there is nothing left to clean,
                        // or a finalization recorded in between would find a cleaner that is already leaving.
                        _isCleaning = false;
                        return;
                    }
                }

                // A failed attempt leaves _lastFinalizedBlock where it was, so stop instead of rescanning the
                // same target forever; the next finalization will start a new attempt.
                if (!CleanProcessedTransactionsDb(target))
                {
                    lock (_cleaningLock) { _isCleaning = false; }
                    return;
                }

                lock (_cleaningLock) { _lastFinalizedBlock = target; }
            }
        }
        catch (Exception exception)
        {
            lock (_cleaningLock) { _isCleaning = false; }
            if (_logger.IsError) _logger.Error("Processed transactions db cleaning loop failed", exception);
        }
    }

    /// <summary>Deletes the processed transactions of every block up to <paramref name="newlyFinalizedBlockNumber"/>.</summary>
    /// <returns><see langword="true"/> when the records were deleted, <see langword="false"/> when the attempt failed.</returns>
    private bool CleanProcessedTransactionsDb(ulong newlyFinalizedBlockNumber)
    {
        // BlobTxStorage tolerates missing payloads during reads; this unsynchronized cleaner must only delete records.
        try
        {
            using (IWriteBatch writeBatch = _processedTxsDb.StartWriteBatch())
            {
                foreach (byte[] key in _processedTxsDb.GetAllKeys())
                {
                    // Individual payload keys contain an eight-byte block number followed by a four-byte index.
                    ulong blockNumber = key.Length == sizeof(ulong) + sizeof(int)
                        ? BinaryPrimitives.ReadUInt64BigEndian(key)
                        : key.ToULongFromBigEndianByteArrayWithoutLeadingZeros();
                    if (newlyFinalizedBlockNumber >= blockNumber)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Cleaning processed blob txs from block {blockNumber}");
                        writeBatch.Remove(key);
                    }
                }
            }

            if (_logger.IsDebug) _logger.Debug($"Cleaned processed blob txs from block {_lastFinalizedBlock} to block {newlyFinalizedBlockNumber}");

            _processedTxsDb.Compact();

            if (_logger.IsDebug) _logger.Debug($"Blob transactions database columns have been compacted");

            return true;
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Couldn't correctly clean db with processed transactions. Newly finalized block {newlyFinalizedBlockNumber}, last finalized block: {_lastFinalizedBlock}", exception);
            return false;
        }
    }

    public void Dispose() => _blockTree.BlocksFinalized -= OnBlocksFinalized;
}
