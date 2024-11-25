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
public class EraImporter : IEraImporter
{
    private readonly IFileSystem _fileSystem;
    private readonly IBlockTree _blockTree;
    private readonly IReceiptStorage _receiptStorage;
    private readonly IBlockValidator _blockValidator;
    private readonly ILogger _logger;
    private readonly int _maxEra1Size;
    private readonly ITunableDb _blocksDb;
    private readonly ITunableDb _receiptsDb;
    private readonly ISyncConfig _syncConfig;
    private readonly IEraStoreFactory _eraStoreFactory;

    public EraImporter(
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
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree;
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _blockValidator = blockValidator;
        _receiptsDb = receiptsDb;
        _blocksDb = blocksDb;
        _eraStoreFactory = eraStoreFactory;
        _logger = logManager.GetClassLogger<EraImporter>();
        _maxEra1Size = eraConfig.MaxEra1Size;
        _syncConfig = syncConfig;
    }

    public async Task Import(string src, long from, long to, string? accumulatorFile, CancellationToken cancellation = default)
    {
        if (!_fileSystem.Directory.Exists(src))
            throw new ArgumentException($"Import directory {src} does not exist", nameof(src));
        if (accumulatorFile != null && !_fileSystem.File.Exists(accumulatorFile))
            throw new ArgumentException($"Accumulator file {accumulatorFile} not exist", nameof(accumulatorFile));

        HashSet<ValueHash256>? trustedAccumulators = null;
        if (accumulatorFile != null)
        {
            string[] lines = _fileSystem.File.ReadAllLines(accumulatorFile);
            trustedAccumulators = lines.Select(s => new ValueHash256(s)).ToHashSet();
        }

        IEraStore eraStore = _eraStoreFactory.Create(src, trustedAccumulators);

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
            throw new EraImportException($"The directory given for import '{src}' have lowest block number {firstBlockInStore} which is lower then last requested block {from}.");
        }
        if (from > to && to != 0)
            throw new ArgumentException("Start block must not be after end block", nameof(from));

        _receiptsDb.Tune(ITunableDb.TuneType.HeavyWrite);
        _blocksDb.Tune(ITunableDb.TuneType.HeavyWrite);

        try
        {
            await ImportInternal(from, to, eraStore, cancellation);
        }
        finally
        {
            _receiptsDb.Tune(ITunableDb.TuneType.Default);
            _blocksDb.Tune(ITunableDb.TuneType.Default);
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

        DateTime lastProgress = DateTime.Now;
        DateTime startTime = DateTime.Now;
        TimeSpan elapsed = TimeSpan.Zero;
        long totalblocks = to - from + 1;
        long blocksProcessed = 0;
        long blocksProcessedAtLastLog = 0;

        using BlockTreeSuggestPacer pacer = new BlockTreeSuggestPacer(_blockTree);
        long blockNumber = from;

        long suggestFromBlock = (_blockTree.Head?.Number ?? 0) + 1;
        if (_syncConfig.FastSync && suggestFromBlock == 1)
        {
            // Its syncing right now. So no state.
            suggestFromBlock = long.MaxValue;
        }

        // Add last header first.
        // This set BestSuggestedHeader so that the receipt insert does not create tx index unnecessarily
        {
            (Block? b, TxReceipt[]? _) = (await eraStore.FindBlockAndReceipts(eraStore.LastBlock, cancellation: cancellation))!;
            if (b?.IsGenesis == false) _blockTree.Insert(b!.Header);
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
        if (from + partitionSize < suggestFromBlock)
        {
            ConcurrentQueue<long> partitionStartBlocks = new ConcurrentQueue<long>();
            for (; blockNumber + partitionSize < suggestFromBlock && blockNumber + partitionSize < to; blockNumber += partitionSize)
            {
                partitionStartBlocks.Enqueue(blockNumber);
            }

            Task[] importTasks = Enumerable.Range(0, 8).Select((_) =>
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
        elapsed = DateTime.Now.Subtract(lastProgress);
        LogImportProgress(DateTime.Now.Subtract(startTime), blocksProcessedAtLastLog, elapsed, blocksProcessed, totalblocks);

        if (_logger.IsInfo) _logger.Info($"Finished history import from {from} to {to}");

        async Task ImportBlock(long blockNumber)
        {
            cancellation.ThrowIfCancellationRequested();

            (Block? b, TxReceipt[]? r) = await eraStore.FindBlockAndReceipts(blockNumber, cancellation: cancellation);
            if (b is null)
            {
                throw new EraImportException($"Unable to find block info for block {blockNumber}");
            }
            if (r is null)
            {
                throw new EraImportException($"Unable to find receipt for block {blockNumber}");
            }
            if (b.Number != blockNumber)
            {
                throw new EraImportException($"Unexpected block number. Expected {blockNumber}. Got {b.Number}");
            }

            if (b.IsGenesis)
            {
                return;
            }

            if (b.IsBodyMissing)
            {
                throw new EraImportException($"Unexpected block without a body found for block number {blockNumber}. Archive might be corrupted.");
            }

            if (suggestFromBlock <= b.Number)
            {
                await pacer.WaitForQueue(b.Number, cancellation);
                await SuggestAndProcessBlock(b);
            }
            else
                InsertBlockAndReceipts(b, r);

            blocksProcessed++;
            if (blocksProcessed % 10000 == 0)
            {
                elapsed = DateTime.Now.Subtract(lastProgress);
                LogImportProgress(DateTime.Now.Subtract(startTime), blocksProcessed - blocksProcessedAtLastLog, elapsed, blocksProcessed, totalblocks);
                lastProgress = DateTime.Now;
                blocksProcessedAtLastLog = blocksProcessed;
            }
        }
    }

    private void LogImportProgress(
        TimeSpan elapsed,
        long blocksProcessedSinceLast,
        TimeSpan elapsedSinceLastLog,
        long totalBlocksProcessed,
        long totalBlocks)
    {
        if (_logger.IsInfo)
            _logger.Info($"Import progress: | {totalBlocksProcessed,10}/{totalBlocks} blocks  | elapsed {elapsed:hh\\:mm\\:ss} | {blocksProcessedSinceLast / elapsedSinceLastLog.TotalSeconds,10:0.00} Blk/s ");
    }

    private void InsertBlockAndReceipts(Block b, TxReceipt[] r)
    {
        if (_blockTree.FindBlock(b.Number) is null)
            _blockTree.Insert(b, BlockTreeInsertBlockOptions.SaveHeader | BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks, bodiesWriteFlags: WriteFlags.DisableWAL);
        if (!_receiptStorage.HasBlock(b.Number, b.Hash!))
            _receiptStorage.Insert(b, r, true, writeFlags: WriteFlags.DisableWAL);
    }

    private async Task SuggestAndProcessBlock(Block block)
    {
        // Confusingly it will get the header with `BlockTreeLookupOptions.TotalDifficultyNotNeeded` then
        // proceed to validate the total difficulty if its not null with the code
        // `parent.TotalDifficulty + header.Difficulty != header.TotalDifficulty`
        // when clearly, the `parent.TotalDifficulty` is going to be null or 0.
        block.Header.TotalDifficulty = null;

        // Should this be in suggest instead?
        if (!_blockValidator.ValidateSuggestedBlock(block, out string? error))
        {
            throw new EraVerificationException($"Block validation failed: {error}");
        }

        var addResult = await _blockTree.SuggestBlockAsync(block, BlockTreeSuggestOptions.ShouldProcess);
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
