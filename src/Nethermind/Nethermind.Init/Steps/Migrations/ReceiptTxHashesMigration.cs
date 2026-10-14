// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Init.Steps.Migrations;

/// <summary>
/// Rewrites stored compact receipts so that they carry the block's transaction hashes, which lets log queries skip
/// the block body read. Receipts written by this version already carry them; this pass covers older ones.
/// </summary>
/// <remarks>
/// Walks from the head down to the lowest stored block and keeps its position, so a restart continues where it
/// stopped. It yields to block processing between small chunks. A value is rewritten only when it is compact without
/// hashes; the receipt bytes are copied as they are, and the hashes are a function of the block hash in the key, so a
/// concurrent write of the same block stores the same content.
/// </remarks>
public sealed class ReceiptTxHashesMigration(
    IReceiptConfig receiptConfig,
    IColumnsDb<ReceiptsColumns> receiptsDb,
    IBlockStore blockStore,
    IBlockTree blockTree,
    IChainLevelInfoRepository chainLevels,
    ISyncModeSelector syncModeSelector,
    IBlockProcessingQueue blockProcessingQueue,
    ILogManager logManager) : IDatabaseMigration, IAsyncDisposable
{
    internal const int ChunkSize = 64;
    private static readonly TimeSpan ProcessingPause = TimeSpan.FromMilliseconds(200);
    private static readonly Hash256 NextBlockKey = Keccak.Compute("ReceiptTxHashesMigrationNextBlock");
    private const ulong Completed = 0;

    private readonly ILogger _logger = logManager.GetClassLogger<ReceiptTxHashesMigration>();
    private readonly IDb _receipts = receiptsDb.GetColumnDb(ReceiptsColumns.Blocks);
    private readonly IDb _metadata = receiptsDb.GetColumnDb(ReceiptsColumns.Default);
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _linked;
    private Task _migration = Task.CompletedTask;

    internal Task Completion => _migration;

    /// <summary>Starts the pass in the background, so that node initialization does not wait for it.</summary>
    public Task Run(CancellationToken cancellationToken)
    {
        if (receiptConfig.StoreReceipts && receiptConfig.CompactReceiptStore && receiptConfig.ReceiptsTxHashesMigration)
        {
            _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            CancellationToken token = _linked.Token;
            _migration = Task.Run(() => RunInBackground(token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private async Task RunInBackground(CancellationToken cancellationToken)
    {
        try
        {
            _ = syncModeSelector.Current;
            await Migrate(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Adding transaction hashes to stored receipts failed; log queries read block bodies for the remaining blocks.", e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _migration;
        _linked?.Dispose();
        _stop.Dispose();
    }

    private async Task Migrate(CancellationToken cancellationToken)
    {
        ulong next = ReadNextBlock() ?? blockTree.Head?.Number ?? Completed;
        ulong lowest = 1;
        if (next < lowest) return;

        if (_logger.IsInfo) _logger.Info($"Adding transaction hashes to stored receipts, blocks {lowest} to {next}.");

        long rewritten = 0;
        long bytesAdded = 0;
        while (next >= lowest && !cancellationToken.IsCancellationRequested)
        {
            await WaitWhileProcessing(cancellationToken);

            ulong chunkEnd = next >= lowest + ChunkSize ? next - ChunkSize + 1 : lowest;
            using (IWriteBatch batch = _receipts.StartWriteBatch())
            {
                for (ulong number = next; number >= chunkEnd; number--)
                {
                    (int blocks, long bytes) = MigrateLevel(number, batch);
                    rewritten += blocks;
                    bytesAdded += bytes;
                }
            }

            next = chunkEnd - 1;
            WriteNextBlock(next < lowest ? Completed : next);
        }

        if (_logger.IsInfo && next < lowest)
            _logger.Info($"Transaction hashes added to stored receipts of {rewritten} blocks, {bytesAdded} bytes added.");
    }

    /// <returns>The number of rewritten blocks and the bytes their values grew by.</returns>
    internal (int Blocks, long Bytes) MigrateLevel(ulong number, IWriteBatch batch)
    {
        ChainLevelInfo? level = chainLevels.LoadLevel(number);
        if (level is null) return (0, 0);

        int rewritten = 0;
        long bytesAdded = 0;
        foreach (BlockInfo blockInfo in level.BlockInfos)
        {
            int added = MigrateBlock(number, blockInfo.BlockHash, batch);
            if (added < 0) continue;

            rewritten++;
            bytesAdded += added;
        }

        return (rewritten, bytesAdded);
    }

    /// <returns>The bytes the value grew by, or -1 when it was not rewritten.</returns>
    private int MigrateBlock(ulong number, Hash256 blockHash, IWriteBatch batch)
    {
        byte[] key = new byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(number, blockHash, key);
        byte[]? receipts = _receipts.Get(key);
        if (receipts is null)
        {
            receipts = _receipts.Get(blockHash.Bytes);
            key = blockHash.BytesToArray();
        }

        if (receipts is null || !ReceiptArrayStorageDecoder.IsCompactEncodingWithoutTxHashes(receipts)) return -1;

        ReceiptRecoveryBlock? recoveryBlock = blockStore.GetReceiptRecoveryBlock(number, blockHash);
        if (recoveryBlock is null) return -1;

        ReceiptRecoveryBlock block = recoveryBlock.Value;
        try
        {
            byte[] txHashes = new byte[block.TransactionCount * Hash256.Size];
            for (int i = 0; i < block.TransactionCount; i++)
            {
                block.GetNextTransactionValueHash().Bytes.CopyTo(txHashes.AsSpan(i * Hash256.Size));
            }

            byte[] migrated = ReceiptArrayStorageDecoder.AddTxHashes(receipts, txHashes);
            batch[key] = migrated;
            return migrated.Length - receipts.Length;
        }
        finally
        {
            block.Dispose();
        }
    }

    private async Task WaitWhileProcessing(CancellationToken cancellationToken)
    {
        while (blockProcessingQueue.IsProcessingBlocks(1))
        {
            await Task.Delay(ProcessingPause, cancellationToken);
        }
    }

    private ulong? ReadNextBlock()
    {
        byte[]? value = _metadata.Get(NextBlockKey.Bytes);
        return value is null ? null : value.AsSpan().ToULongFromBigEndianByteArrayWithoutLeadingZeros();
    }

    private void WriteNextBlock(ulong next) =>
        _metadata.PutSpan(NextBlockKey.Bytes, next.ToBigEndianSpanWithoutLeadingZeros(out _));
}
