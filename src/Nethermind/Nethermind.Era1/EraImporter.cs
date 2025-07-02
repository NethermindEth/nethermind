// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.IO.Abstractions;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Era1.Exceptions;
using Nethermind.Logging;

namespace Nethermind.Era1;
public class EraImporter(
    IFileSystem fileSystem,
    IBlockTree blockTree,
    IReceiptStorage receiptStorage,
    IBlockValidator blockValidator,
    ILogManager logManager,
    IEraConfig eraConfig,
    ISyncConfig syncConfig,
    IEraStoreFactory eraStoreFactory,
    [KeyFilter(DbNames.Blocks)] ITunableDb blocksDb,
    [KeyFilter(DbNames.Receipts)] ITunableDb receiptsDb)
    : IEraImporter
{

    private readonly ILogger _logger = logManager.GetClassLogger<EraImporter>();
    private readonly int _maxEra1Size = eraConfig.MaxEra1Size;

    public async Task Import(string src, long from, long to, string? accumulatorFile, CancellationToken cancellation = default)
    {
        if (!fileSystem.Directory.Exists(src))
            throw new ArgumentException($"Import directory {src} does not exist");
        if (accumulatorFile != null && !fileSystem.File.Exists(accumulatorFile))
            throw new ArgumentException($"Accumulator file {accumulatorFile} not exist");

        HashSet<ValueHash256>? trustedAccumulators = null;
        if (accumulatorFile != null)
        {
            trustedAccumulators = fileSystem.File.ReadAllLines(accumulatorFile).Select(s => new ValueHash256(s)).ToHashSet();
        }

        IEraStore eraStore = eraStoreFactory.Create(src, trustedAccumulators);

        long lastBlockInStore = eraStore.LastBlock;
        if (to == 0) to = long.MaxValue;
        if (to != long.MaxValue && lastBlockInStore < to)
        {
            throw new EraImportException($"The directory given for import '{src}' have highest block number {lastBlockInStore} which is lower then last requested block {to}.");
        }
        if (to == long.MaxValue)
        {
            to = lastBlockInStore;
        }

        long firstBlockInStore = eraStore.FirstBlock;
        if (from == 0 && firstBlockInStore != 0)
        {
            from = firstBlockInStore;
        }
        else if (from < firstBlockInStore)
        {
            throw new EraImportException($"The directory given for import '{src}' have lowest block number {firstBlockInStore} which is lower then first requested block {from}.");
        }
        if (from > to && to != 0)
            throw new ArgumentException($"Start block ({from}) must not be after end block ({to})");

        long headp1 = (blockTree.Head?.Number ?? 0) + 1;
        if (from > headp1)
        {
            throw new ArgumentException($"Start block ({from}) must not be after block after head ({headp1})");
        }

        receiptsDb.Tune(ITunableDb.TuneType.HeavyWrite);
        blocksDb.Tune(ITunableDb.TuneType.HeavyWrite);

        try
        {
            await ImportInternal(from, to, eraStore, cancellation);
        }
        finally
        {
            receiptsDb.Tune(ITunableDb.TuneType.Default);
            blocksDb.Tune(ITunableDb.TuneType.Default);
        }
    }

    private async Task ImportInternal(
        long from,
        long to,
        IEraStore eraStore,
        CancellationToken cancellation)
    {
        if (_logger.IsInfo) _logger.Info($"Starting history import from {from} to {to}");

        using IEraStore _ = eraStore;

        ProgressLogger progressLogger = new ProgressLogger("Era import", logManager);
        progressLogger.Reset(0, to - from + 1);
        long blocksProcessed = 0;

        using BlockTreeSuggestPacer pacer = new BlockTreeSuggestPacer(blockTree, eraConfig.ImportBlocksBufferSize, eraConfig.ImportBlocksBufferSize - 1024);
        long blockNumber = from;

        long suggestFromBlock = (blockTree.Head?.Number ?? 0) + 1;
        if (syncConfig.FastSync && suggestFromBlock == 1)
        {
            // Its syncing right now. So no state.
            suggestFromBlock = long.MaxValue;
        }

        // I wish I could say that EraStore can be run used in parallel in any way you like but I could not make it so.
        // This make the `blockNumber` aligned to era file boundary so that when running parallel, each thread does not
        // work on the same era file as other thread.
        long nextEraStart = eraStore.NextEraStart(blockNumber);
        if (nextEraStart <= to)
        {
            for (; blockNumber < nextEraStart; blockNumber++)
            {
                await ImportBlock(blockNumber);
            }
        }

        // Earlier part can be parallelized
        long partitionSize = _maxEra1Size;
        if (blockNumber + partitionSize < suggestFromBlock)
        {
            ConcurrentQueue<long> partitionStartBlocks = new ConcurrentQueue<long>();
            for (; blockNumber + partitionSize < suggestFromBlock && blockNumber + partitionSize < to; blockNumber += partitionSize)
            {
                partitionStartBlocks.Enqueue(blockNumber);
            }

            Task[] importTasks = Enumerable.Range(0, (eraConfig.Concurrency == 0 ? Environment.ProcessorCount : eraConfig.Concurrency)).Select((_) =>
            {
                return Task.Run(async () =>
                {
                    while (partitionStartBlocks.TryDequeue(out long partitionStartBlock))
                    {
                        for (long i = 0; i < partitionSize; i++)
                        {
                            await ImportBlock(i + partitionStartBlock);
                        }
                    }
                });
            }).ToArray();

            await Task.WhenAll(importTasks);
        }

        for (; blockNumber <= to; blockNumber++)
        {
            await ImportBlock(blockNumber);
        }
        progressLogger.LogProgress();

        if (_logger.IsInfo) _logger.Info($"Finished history import from {from} to {to}");

        async Task ImportBlock(long blockNumber)
        {
            if (_logger.IsTrace) _logger.Trace($"Importing block {blockNumber}");
            cancellation.ThrowIfCancellationRequested();

            (Block? block, TxReceipt[]? receipt) = await eraStore.FindBlockAndReceipts(blockNumber, cancellation: cancellation);
            if (block is null)
            {
                throw new EraImportException($"Unable to find block info for block {blockNumber}");
            }
            if (receipt is null)
            {
                throw new EraImportException($"Unable to find receipt for block {blockNumber}");
            }
            if (block.Number != blockNumber)
            {
                throw new EraImportException($"Unexpected block number. Expected {blockNumber}. Got {block.Number}");
            }

            if (block.IsGenesis)
            {
                return;
            }

            if (block.IsBodyMissing)
            {
                throw new EraImportException($"Unexpected block without a body found for block number {blockNumber}. Archive might be corrupted.");
            }

            if (suggestFromBlock <= block.Number)
            {
                await pacer.WaitForQueue(block.Number, cancellation);
                await SuggestAndProcessBlock(block);
            }
            else
                InsertBlockAndReceipts(block, receipt, to);

            long processed = Interlocked.Increment(ref blocksProcessed);
            if (processed % 10000 == 0)
            {
                progressLogger.Update(processed);
                progressLogger.LogProgress();
            }
        }
    }

    private void InsertBlockAndReceipts(Block b, TxReceipt[] r, long lastBlockNumber)
    {
        if (blockTree.FindBlock(b.Number) is null)
            blockTree.Insert(b, BlockTreeInsertBlockOptions.SaveHeader | BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks, bodiesWriteFlags: WriteFlags.DisableWAL);
        if (!receiptStorage.HasBlock(b.Number, b.Hash!))
            receiptStorage.Insert(b, r, true, writeFlags: WriteFlags.DisableWAL, lastBlockNumber: lastBlockNumber);
    }

    private async Task SuggestAndProcessBlock(Block block)
    {
        // Confusingly it will get the header with `BlockTreeLookupOptions.TotalDifficultyNotNeeded` then
        // proceed to validate the total difficulty if its not null with the code
        // `parent.TotalDifficulty + header.Difficulty != header.TotalDifficulty`
        // when clearly, the `parent.TotalDifficulty` is going to be null or 0.
        block.Header.TotalDifficulty = null;

        // Should this be in suggest instead?
        if (!blockValidator.ValidateSuggestedBlock(block, out string? error))
        {
            throw new EraVerificationException($"Block validation failed: {error}");
        }

        var addResult = await blockTree.SuggestBlockAsync(block, BlockTreeSuggestOptions.ShouldProcess);
        switch (addResult)
        {
            case AddBlockResult.AlreadyKnown:
                return;
            case AddBlockResult.CannotAccept:
                throw new EraImportException("Rejected block in Era1 archive");
            case AddBlockResult.UnknownParent:
                throw new EraImportException("Unknown parent for block in Era1 archive");
            case AddBlockResult.InvalidBlock:
                throw new EraImportException("Invalid block in Era1 archive");
            case AddBlockResult.Added:
                // Hmm... this is weird. Could be beacon body. In any the head should be before this block
                // so it should get to this block eventually.
                break;
            default:
                throw new NotSupportedException($"Not supported value of {nameof(AddBlockResult)} = {addResult}");
        }
    }
}
