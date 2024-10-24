// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac.Features.AttributeFilters;
using Microsoft.FSharp.Core;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Era1.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

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
    private readonly ReceiptMessageDecoder _receiptDecoder;
    private readonly ILogger _logger;

    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(10);

    public EraImporter(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        ILogManager logManager,
        [KeyFilter(EraComponentKeys.NetworkName)] string networkName)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree;
        _blockValidator = blockValidator;
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _receiptDecoder = new();
        _logger = logManager.GetClassLogger<EraImporter>();
        if (string.IsNullOrWhiteSpace(networkName)) throw new ArgumentException("Cannot be null or whitespace.", nameof(specProvider));
        _networkName = networkName.Trim().ToLower();
    }

    public async Task Import(string src, long start, long end, string? accumulatorFile, CancellationToken cancellation = default)
    {
        // TODO: End not handled missing

        if (_logger.IsInfo) _logger.Info($"Starting history import from {start} to {end}");
        if (!string.IsNullOrEmpty(accumulatorFile))
        {
            await VerifyEraFiles(src, accumulatorFile, cancellation);
        }
        await ImportInternal(src, start, end, false, cancellation);

        if (_logger.IsInfo) _logger.Info($"Finished history import from {start} to {end}");
    }

    public Task ImportAsArchiveSync(string src, CancellationToken cancellation)
    {
        _logger.Info($"Starting full archive import from '{src}'");
        return ImportInternal(src, _blockTree.Head?.Number + 1 ?? 0, long.MaxValue, true, cancellation);
    }

    private async Task ImportInternal(
        string src,
        long startNumber,
        long end,
        bool processBlock,
        CancellationToken cancellation)
    {
        if (!_fileSystem.Directory.Exists(src))
        {
            throw new EraImportException($"The directory given for import '{src}' does not exist.");
        }

        using EraStore eraStore = new(src, _networkName, _fileSystem);

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

            (Block? b, TxReceipt[]? r) = await eraStore.FindBlockAndReceipts(blockNumber, cancellation);
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

            if (processBlock)
            {
                await pacer.WaitForQueue(b.Number, cancellation);
                await SuggestBlock(b, r, processBlock);
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

    private async Task SuggestBlock(Block block, TxReceipt[] receipts, bool processBlock)
    {
        // Well... this is weird
        block.Header.TotalDifficulty = null;

        if (!_blockValidator.ValidateSuggestedBlock(block, out string? error))
        {
            throw new EraImportException($"Invalid block in Era1 archive. {error}");
        }

        var options = processBlock ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None;
        var addResult = await _blockTree.SuggestBlockAsync(block, options);
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
                if (!processBlock) _receiptStorage.Insert(block, receipts);
                break;
            default:
                throw new NotSupportedException($"Not supported value of {nameof(AddBlockResult)} = {addResult}");
        }
    }


    /// <summary>
    /// Verifies all era1 archives from a directory, with an expected accumulator list from a hex encoded file.
    /// </summary>
    /// <param name="eraDirectory"></param>
    /// <param name="accumulatorFile"></param>
    /// <param name="cancellation"></param>
    /// <exception cref="EraVerificationException">If the verification fails.</exception>
    public async Task VerifyEraFiles(string eraDirectory, string accumulatorFile, CancellationToken cancellation = default)
    {
        if (!_fileSystem.Directory.Exists(eraDirectory))
            throw new EraImportException($"Directory does not exist '{eraDirectory}'");
        if (!_fileSystem.File.Exists(accumulatorFile))
            throw new EraImportException($"Accumulator file does not exist '{accumulatorFile}'");

        var eraStore = new EraStore(eraDirectory, _networkName, _fileSystem);

        string[] lines = await _fileSystem.File.ReadAllLinesAsync(accumulatorFile, cancellation);
        var accumulators = lines.Select(s => new ValueHash256(s)).ToHashSet();
        await eraStore.VerifyAll(_specProvider, cancellation, accumulators, LogVerificationProgress);
    }

    private void ValidateReceipts(Block block, TxReceipt[] blockReceipts)
    {
        Hash256 receiptsRoot = new ReceiptTrie<TxReceipt>(_specProvider.GetSpec(block.Header), blockReceipts, _receiptDecoder).RootHash;

        if (receiptsRoot != block.ReceiptsRoot)
        {
            throw new EraImportException($"Wrong receipts root in Era1 archive for block {block.ToString(Block.Format.Short)}.");
        }
    }

    private void LogVerificationProgress(VerificationProgressArgs args)
    {
        if (_logger.IsInfo)
            _logger.Info($"Verification progress: {args.Processed,10}/{args.TotalToProcess} archives  |  elapsed {args.Elapsed:hh\\:mm\\:ss}  |  {args.Processed / args.Elapsed.TotalSeconds,10:0.00} archives/s");
    }
}
