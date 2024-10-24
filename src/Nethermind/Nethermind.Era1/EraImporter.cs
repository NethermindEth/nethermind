// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Era1;
public class EraImporter : IEraImporter
{
    private const int MergeBlock = 15537393;
    private readonly IFileSystem _fileSystem;
    private readonly IBlockTree _blockTree;
    private readonly IBlockValidator _blockValidator;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ISpecProvider _specProvider;
    private readonly string _networkName;
    private readonly ILogger _logger;
    private readonly int _maxEra1Size;

    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(10);

    public EraImporter(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        ILogManager logManager,
        IEraConfig eraConfig,
        [KeyFilter(EraComponentKeys.NetworkName)] string networkName)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree;
        _blockValidator = blockValidator;
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _logger = logManager.GetClassLogger<EraImporter>();
        if (string.IsNullOrWhiteSpace(networkName)) throw new ArgumentException("Cannot be null or whitespace.", nameof(specProvider));
        _networkName = networkName.Trim().ToLower();
        _maxEra1Size = eraConfig.MaxEra1Size;
    }

    public async Task Import(string src, long start, long end, string? accumulatorFile, CancellationToken cancellation = default)
    {
        // TODO: End not handled missing

        if (_logger.IsInfo) _logger.Info($"Starting history import from {start} to {end}");
        await ImportInternal(src, start, end, accumulatorFile, false, cancellation);

        if (_logger.IsInfo) _logger.Info($"Finished history import from {start} to {end}");
    }

    public Task ImportAsArchiveSync(string src, string? accumulatorFile, CancellationToken cancellation)
    {
        _logger.Info($"Starting full archive import from '{src}'");
        return ImportInternal(src, _blockTree.Head?.Number + 1 ?? 0, long.MaxValue, accumulatorFile, true, cancellation);
    }

    private async Task ImportInternal(
        string src,
        long startNumber,
        long end,
        string? accumulatorFile,
        bool processBlock,
        CancellationToken cancellation)
    {
        if (!_fileSystem.Directory.Exists(src))
        {
            throw new EraImportException($"The directory given for import '{src}' does not exist.");
        }

        HashSet<ValueHash256>? trustedAccumulators = null;
        if (accumulatorFile != null)
        {
            string[] lines = await _fileSystem.File.ReadAllLinesAsync(accumulatorFile, cancellation);
            trustedAccumulators = lines.Select(s => new ValueHash256(s)).ToHashSet();
        }
        using EraStore eraStore = new(src, trustedAccumulators, _specProvider, _networkName, _fileSystem, _maxEra1Size);

        long lastBlockInStore = eraStore.LastBlock;
        if (end != long.MaxValue && lastBlockInStore < end)
        {
            throw new EraImportException($"The directory given for import '{src}' have highest block number {lastBlockInStore} which is lower then last requested block {end}.");
        }
        if (end == long.MaxValue)
        {
            end = lastBlockInStore;
        }

        DateTime lastProgress = DateTime.Now;
        DateTime startTime = DateTime.Now;
        long totalblocks = end - startNumber + 1;
        int blocksProcessed = 0;

        using BlockTreeSuggestPacer pacer = new BlockTreeSuggestPacer(_blockTree);

        for (long blockNumber = startNumber; blockNumber <= end; blockNumber++)
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

            if (b.IsGenesis)
            {
                continue;
            }

            if (b.IsBodyMissing)
            {
                throw new EraImportException($"Unexpected block without a body found for block number {blockNumber}. Archive might be corrupted.");
            }

            if (processBlock && (_blockTree.Head?.Number ?? 0) < b.Number)
            {
                await pacer.WaitForQueue(b.Number, cancellation);
                await SuggestAndProcessBlock(b);
            }
            else
                InsertBlockAndReceipts(b, r);

            blocksProcessed++;
            TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
            if (elapsed > ProgressInterval)
            {
                LogImportProgress(DateTime.Now.Subtract(startTime), blocksProcessed, totalblocks);
                lastProgress = DateTime.Now;
            }
        }
        LogImportProgress(DateTime.Now.Subtract(startTime), blocksProcessed, totalblocks);
    }

    private void LogImportProgress(
        TimeSpan elapsed,
        long totalBlocksProcessed,
        long totalBlocks)
    {
        if (_logger.IsInfo)
            _logger.Info($"Import progress: | {totalBlocksProcessed,10}/{totalBlocks} blocks  | elapsed {elapsed:hh\\:mm\\:ss}");
    }

    private void InsertBlockAndReceipts(Block b, TxReceipt[] r)
    {
        _blockTree.Insert(b, BlockTreeInsertBlockOptions.SaveHeader | BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks, bodiesWriteFlags: WriteFlags.DisableWAL);
        _receiptStorage.Insert(b, r);
    }

    private async Task SuggestAndProcessBlock(Block block)
    {
        // Well... this is weird
        block.Header.TotalDifficulty = null;

        if (!_blockValidator.ValidateSuggestedBlock(block, out string? error))
        {
            throw new EraImportException($"Invalid block in Era1 archive. {error}");
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
