// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
using Nethermind.EraE.Config;
using EraImportException = Nethermind.Era1.EraImportException;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;
using Nethermind.EraE.Export;
using Nethermind.EraE.Store;
using Nethermind.Logging;

namespace Nethermind.EraE.Import;

public sealed class EraImporter(
    IFileSystem fileSystem,
    IBlockTree blockTree,
    IReceiptStorage receiptStorage,
    IBlockValidator blockValidator,
    ILogManager logManager,
    IEraEConfig eraConfig,
    ISyncConfig syncConfig,
    IEraStoreFactory eraStoreFactory,
    [KeyFilter(DbNames.Blocks)] ITunableDb blocksDb,
    [KeyFilter(DbNames.Receipts)] ITunableDb receiptsDb)
    : IEraImporter
{
    private readonly ILogger _logger = logManager.GetClassLogger<EraImporter>();
    private readonly int _maxEraSize = eraConfig.MaxEraSize;

    private const int ProgressLogInterval = 10000;

    public async Task Import(string src, long from, long to, string? accumulatorFile, CancellationToken cancellation = default)
    {
        if (!fileSystem.Directory.Exists(src))
        {
            if (string.IsNullOrEmpty(eraConfig.RemoteBaseUrl))
                throw new ArgumentException($"Import source directory does not exist: {src}");
            fileSystem.Directory.CreateDirectory(src);
        }
        if (accumulatorFile != null && !fileSystem.File.Exists(accumulatorFile))
            throw new ArgumentException($"Accumulator file {accumulatorFile} does not exist.");

        ISet<ValueHash256>? trustedAccumulators = null;
        if (accumulatorFile != null)
        {
            HashSet<ValueHash256> accumulators = [];
            foreach (string line in await fileSystem.File.ReadAllLinesAsync(accumulatorFile, cancellation))
                accumulators.Add(EraPathUtils.ExtractHashFromChecksumEntry(line));
            trustedAccumulators = accumulators;
        }

        using IEraStore eraStore = eraStoreFactory.Create(src, trustedAccumulators);

        (long firstBlockInStore, long lastBlockInStore) = eraStore.BlockRange;
        if (to is 0 or long.MaxValue)
            to = lastBlockInStore;
        else if (to > lastBlockInStore)
            throw new EraImportException($"Store highest block {lastBlockInStore} is lower than requested end {to}.");

        if (from == 0)
            from = firstBlockInStore;
        else if (from < firstBlockInStore)
            throw new EraImportException($"Store first block {firstBlockInStore} is higher than requested start {from}.");
        if (from > to)
            throw new ArgumentException($"Start block ({from}) must not be after end block ({to}).");

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

    private async Task ImportInternal(long from, long to, IEraStore eraStore, CancellationToken cancellation)
    {
        if (_logger.IsInfo) _logger.Info($"Starting EraE import from {from} to {to}");

        ProgressLogger progressLogger = new("EraE import", logManager);
        progressLogger.Reset(0, to - from + 1);
        long blocksProcessed = 0;

        using BlockTreeSuggestPacer pacer = new(blockTree, eraConfig.ImportBlocksBufferSize, eraConfig.ImportBlocksBufferSize - 1024);
        long blockNumber = from;

        long suggestFromBlock = (blockTree.Head?.Number ?? 0) + 1;
        if (syncConfig.FastSync && suggestFromBlock == 1)
            suggestFromBlock = long.MaxValue;

        // Align to era boundary for parallel section
        long nextEraStart = eraStore.NextEraStart(blockNumber);
        if (nextEraStart <= to)
        {
            for (; blockNumber < nextEraStart; blockNumber++)
                await ImportBlock(blockNumber);
        }

        // Parallel historical import (blocks without state)
        long partitionSize = _maxEraSize;
        if (blockNumber + partitionSize < suggestFromBlock)
        {
            ConcurrentQueue<long> partitionStartBlocks = new();
            for (; blockNumber + partitionSize < suggestFromBlock && blockNumber + partitionSize < to; blockNumber += partitionSize)
                partitionStartBlocks.Enqueue(blockNumber);

            int concurrency = eraConfig.Concurrency == 0 ? Environment.ProcessorCount : eraConfig.Concurrency;
            Task[] workers = new Task[concurrency];
            for (int i = 0; i < concurrency; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    while (partitionStartBlocks.TryDequeue(out long partStart))
                    {
                        for (long j = 0; j < partitionSize; j++)
                        {
                            await ImportBlock(partStart + j);
                        }
                    }
                }, cancellation);
            }

            await Task.WhenAll(workers);
        }

        // Sequential near-head import
        for (; blockNumber <= to; blockNumber++)
            await ImportBlock(blockNumber);

        progressLogger.LogProgress();
        if (_logger.IsInfo) _logger.Info($"Finished EraE import from {from} to {to}");

        async Task ImportBlock(long number)
        {
            if (_logger.IsTrace) _logger.Trace($"Importing EraE block {number}");
            cancellation.ThrowIfCancellationRequested();

            (Block? block, TxReceipt[]? receipts) = await eraStore.FindBlockAndReceipts(number, cancellation: cancellation);

            if (block is null)
                throw new EraImportException($"Block {number} not found in archive.");
            if (receipts is null)
                throw new EraImportException($"Receipts for block {number} not found in archive.");
            if (block.Number != number)
                throw new EraImportException($"Unexpected block number {block.Number}, expected {number}.");
            if (block.IsGenesis)
                return;
            if (block.IsBodyMissing)
                throw new EraImportException($"Block {number} body is missing — archive may be corrupted.");

            if (suggestFromBlock <= block.Number)
            {
                await pacer.WaitForQueue(block.Number, cancellation);
                await SuggestAndProcessBlock(block);
            }
            else
            {
                InsertBlockAndReceipts(block, receipts, to);
            }

            long processed = Interlocked.Increment(ref blocksProcessed);
            if (processed % ProgressLogInterval == 0)
            {
                progressLogger.Update(processed);
                progressLogger.LogProgress();
            }
        }
    }

    private void InsertBlockAndReceipts(Block block, TxReceipt[] receipts, long lastBlockNumber)
    {
        Block? existing = blockTree.FindBlock(block.Number);
        if (existing is null)
        {
            BlockTreeInsertHeaderOptions headerOptions = block.Header.IsPostMerge
                ? BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded
                : BlockTreeInsertHeaderOptions.None;
            AddBlockResult result = blockTree.Insert(block, BlockTreeInsertBlockOptions.SaveHeader | BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks, headerOptions, bodiesWriteFlags: WriteFlags.DisableWAL);
            if (result is not AddBlockResult.Added and not AddBlockResult.AlreadyKnown)
                if (_logger.IsWarn) _logger.Warn($"Unexpected block tree insert result {result} for block {block.Number}.");
        }
        else if (!block.Header.IsPostMerge && existing.TotalDifficulty is null)
        {
            // Block body already exists (e.g. downloaded during snap sync ancient-bodies phase) but
            // TotalDifficulty was not stored at that time. Re-insert so the block tree stores the
            // correct TD — either from the era file (if available) or computed via SetTotalDifficulty.
            AddBlockResult result = blockTree.Insert(block, BlockTreeInsertBlockOptions.SaveHeader | BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks, bodiesWriteFlags: WriteFlags.DisableWAL);
            if (result is not AddBlockResult.Added and not AddBlockResult.AlreadyKnown)
                if (_logger.IsWarn) _logger.Warn($"Unexpected block tree insert result {result} for block {block.Number} (TD re-insert).");
        }
        if (!receiptStorage.HasBlock(block.Number, block.Hash!))
            receiptStorage.Insert(block, receipts, true, writeFlags: WriteFlags.DisableWAL, lastBlockNumber: lastBlockNumber);
    }

    private async Task SuggestAndProcessBlock(Block block)
    {
        block.Header.TotalDifficulty = null;

        if (!blockValidator.ValidateBodyAgainstHeader(block.Header, block.Body, out string? error))
            throw new EraVerificationException($"Block validation failed: {error}");

        AddBlockResult addResult = await blockTree.SuggestBlockAsync(block, BlockTreeSuggestOptions.ShouldProcess);
        switch (addResult)
        {
            case AddBlockResult.AlreadyKnown:
                return;
            case AddBlockResult.CannotAccept:
                throw new EraImportException("Block rejected from EraE archive.");
            case AddBlockResult.UnknownParent:
                throw new EraImportException("Unknown parent for block in EraE archive.");
            case AddBlockResult.InvalidBlock:
                throw new EraImportException("Invalid block in EraE archive.");
            case AddBlockResult.Added:
                break;
            default:
                throw new NotSupportedException($"Unexpected {nameof(AddBlockResult)}: {addResult}");
        }
    }
}
