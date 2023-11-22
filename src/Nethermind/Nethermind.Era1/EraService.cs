// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.State.Repositories;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Era1;
public class EraService : IEraService
{
    private const int MergeBlock = 15537393;
    private readonly IFileSystem _fileSystem;
    private readonly IBlockTree _blockTree;
    private readonly IBlockValidator _blockValidator;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    public EraService(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        ILogManager logManager)
    {
        if (logManager is null) throw new ArgumentNullException(nameof(logManager));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _blockValidator = blockValidator;
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _logger = logManager.GetClassLogger();
    }

    public async Task Import(string src, string network)
    {
        var eraFiles = EraReader.GetAllEraFiles(src, network);
        foreach (var era in eraFiles)
        {
            using var eraEnumerator = await EraReader.Create(era);

            await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraEnumerator)
            {

            }
        }
    }

    //TODO cancellation
    public async Task Export(
        string destinationPath,
        string network,
        long start,
        long end,
        int size = EraWriter.MaxEra1Size,
        CancellationToken cancellation = default)
    {
        if (destinationPath is null) throw new ArgumentNullException(nameof(destinationPath));
        if (_fileSystem.File.Exists(destinationPath)) throw new ArgumentException(nameof(destinationPath), $"Cannot be a file.");

        EnsureExistingEraFiles();
        if (!_fileSystem.Directory.Exists(destinationPath))
        {
            //TODO look into permission settings - should it be set?
            _fileSystem.Directory.CreateDirectory(destinationPath);
        }
        if (_logger.IsInfo) _logger.Info($"Starting history export from {start} to {end}");
        DateTime startTime = DateTime.Now;
        DateTime lastProgress = DateTime.Now;
        int processed = 0;
        try
        {
            for (long i = start; i <= end; i += size)
            {
                string filePath = Path.Combine(
                   destinationPath,
                   EraWriter.Filename(network, i / size, Keccak.Zero));
                using EraWriter? builder = EraWriter.Create(_fileSystem.File.Create(filePath), _specProvider);

                //TODO read directly from RocksDb with range reads
                for (var y = i; y < end; y++)
                {
                    //Naive lookup
                    ChainLevelInfo? chainlevel = _blockTree.FindLevel(y);
                    if (chainlevel == null)
                    {
                        throw new EraException($"Cannot find a chain level for block number {y}.");
                    }

                    Block? block = _blockTree.FindBlock(
                        chainlevel.BlockInfos[0].BlockHash,
                        BlockTreeLookupOptions.RequireCanonical | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);

                    if (block == null)
                    {
                        //Found the level but not the block?
                        throw new EraException($"Found Could not find a block with hash {chainlevel.BlockInfos[0].BlockHash}.");
                    }

                    TxReceipt[]? receipts = _receiptStorage.Get(block);
                    if (receipts == null)
                    {
                        //TODO  handle this scenario
                        throw new EraException($"Could not find receipts for block {block.ToString(Block.Format.FullHashAndNumber)}");
                    }
                    if (!await builder.Add(block, receipts, cancellation))
                    {
                        byte[] root = await builder.Finalize();
                        builder.Dispose();
                        string rename = Path.Combine(
                                                destinationPath,
                                                EraWriter.Filename(network, i / size, new Hash256(root)));
                        _fileSystem.File.Move(filePath,
                            rename, true);
                        break;
                    }
                    processed++;
                    TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
                    if (elapsed.TotalSeconds > TimeSpan.FromSeconds(10).TotalSeconds)
                    {
                        if (_logger.IsInfo) _logger.Info($"Export progress: {y,12}/{end}  |  elapsed {DateTime.Now.Subtract(startTime):hh\\:mm\\:ss}  |  {processed / elapsed.TotalSeconds,2:0.##} Blk/s");
                        lastProgress = DateTime.Now;
                        processed = 0;
                    }
                }
            }
            if (_logger.IsInfo) _logger.Info($"Finished history export from {start} to {end}");
        }
        catch (TaskCanceledException)
        {
            _logger.Error($"A running export job was cancelled. Exported archives in {destinationPath} might be in corrupted state.");
        }
        catch (Exception e)
        {
            _logger.Error("Export error", e);
            throw;
        }
    }

    private async Task ImportBlocks(Block[] blocks, TxReceipt[][] receipts, CancellationToken cancellation)
    {
        if (blocks.Length > 0)
            throw new ArgumentException("Cannot be empty.", nameof(blocks));
        if (receipts.Length > 0)
            throw new ArgumentException("Cannot be empty.", nameof(receipts));
        if (blocks.Length != receipts.Length)
            throw new ArgumentException("Must have an equal amount of blocks and receipts.", nameof(blocks));

        if (blocks[0] == null)
            throw new ArgumentException("Cannot contain null.", nameof(blocks));

        //DownloaderOptions options = blocksRequest.Options;
        //bool downloadReceipts = (options & DownloaderOptions.WithReceipts) == DownloaderOptions.WithReceipts;
        //bool shouldProcess = (options & DownloaderOptions.Process) == DownloaderOptions.Process;
        //bool shouldMoveToMain = (options & DownloaderOptions.MoveToMain) == DownloaderOptions.MoveToMain;

        //int blocksSynced = 0;
        //int ancestorLookupLevel = 0;

        //long currentNumber = Math.Max(0, Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1));
        //// pivot number - 6 for uncle validation
        //// long currentNumber = Math.Max(Math.Max(0, pivotNumber - 6), Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1));

        //if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation

        //Block firstBlock = blocks[0];


        //bool parentIsKnown = _blockTree.IsKnownBlock(firstBlock.Number - 1, firstBlock.ParentHash);

        //ancestorLookupLevel = 0;
        //for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        //{
        //    if (cancellation.IsCancellationRequested)
        //    {
        //        if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
        //        break;
        //    }

        //    Block currentBlock = blocks[blockIndex];
        //    TxReceipt[] currentReceipts = receipts[blockIndex];
        //    //if (_logger.IsTrace) _logger.Trace($"Received {currentBlock} from {bestPeer}");

        //    if (currentBlock.IsBodyMissing)
        //    {
        //        throw new InvalidOperationException($"A block without a body was passed.");
        //    }

        //    // can move this to block tree now?
        //    if (!_blockValidator.ValidateSuggestedBlock(currentBlock))
        //    {
        //        throw new EraException($"Era1 archive contains an invalid block {currentBlock.ToString(Block.Format.Short)}.");
        //    }

        //    ValidateReceipts(currentBlock, currentReceipts);

        //    //if (_logger.IsTrace) _logger.Trace($"BlockDownloader - SuggestBlock {currentBlock}, ShouldProcess: {true}");
        //    var addResult = _blockTree.SuggestBlock(currentBlock, shouldProcess ? BlockTreeSuggestOptions.ForceSetAsMain ShouldProcess : BlockTreeSuggestOptions.None);

        //if (HandleAddResult(bestPeer, currentBlock.Header, blockIndex == 0, _blockTree.SuggestBlock(currentBlock, shouldProcess ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None)))
        //{
        //    TryUpdateTerminalBlock(currentBlock.Header, shouldProcess);
        //    if (downloadReceipts)
        //    {
        //        TxReceipt[]? contextReceiptsForBlock = context.ReceiptsForBlocks![blockIndex];
        //        if (contextReceiptsForBlock is not null)
        //        {
        //            _receiptStorage.Insert(currentBlock, contextReceiptsForBlock);
        //        }
        //        else
        //        {
        //            // this shouldn't now happen with new validation above, still lets keep this check
        //            if (currentBlock.Header.HasTransactions)
        //            {
        //                if (_logger.IsError) _logger.Error($"{currentBlock} is missing receipts");
        //            }
        //        }
        //    }

        //    blocksSynced++;
        //}
    //}

//        if (shouldMoveToMain)
//        {
//            _blockTree.UpdateMainChain(new[] { currentBlock
//          }, false);
        

//        currentNumber += 1;


//        if (blocksSynced > 0)
//        {
//            _syncReport.FullSyncBlocksDownloaded.Update(_blockTree.BestSuggestedHeader?.Number ?? 0);
//            _syncReport.FullSyncBlocksKnown = bestPeer.HeadNumber;
//        }

    }

    private void ValidateReceipts(Block block, TxReceipt[] blockReceipts)
    {
        Hash256 receiptsRoot = new ReceiptTrie(_specProvider.GetSpec(block.Header), blockReceipts).RootHash;

        if (receiptsRoot != block.ReceiptsRoot)
        {
            throw new EraException($"Wrong receipts root in Era1 archive for block {block.ToString(Block.Format.Short)}.");
        }
    }

    private int EnsureExistingEraFiles()
    {
        //TODO check and handle existing ERA files in case this is a restart
        //What is the correct behavior? 
        return 0;
    }

    public bool VerifyEraFiles(string path)
    {
        throw new NotImplementedException();
    }
}
