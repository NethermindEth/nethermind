// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Text;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization;
using Org.BouncyCastle.Utilities.Encoders;
using MathNet.Numerics.LinearAlgebra.Factorization;
using System.Diagnostics;
using System.Linq;
using System.Buffers;
using Nethermind.Core.Collections;

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

        try
        {
            if (!_fileSystem.Directory.Exists(destinationPath))
            {
                //TODO look into permission settings - should it be set?
                _fileSystem.Directory.CreateDirectory(destinationPath);
            }
            if (_logger.IsInfo) _logger.Info($"Starting history export from {start} to {end}");

            using StreamWriter checksumWriter = _fileSystem.File.CreateText(Path.Combine(destinationPath, "checksums.txt"));

            DateTime startTime = DateTime.Now;
            DateTime lastProgress = DateTime.Now;
            int processed = 0;

            for (long i = start; i <= end; i += size)
            {
                string filePath = Path.Combine(
                   destinationPath,
                   EraWriter.Filename(network, i / size, Keccak.Zero));
                using EraWriter? builder = EraWriter.Create(_fileSystem.File.Create(filePath), _specProvider);

                //For compatibility reasons, we dont want to write a line termninator after the last checksum,
                //so we write one here instead, avoiding the last line
                if (i != start)
                    await checksumWriter.WriteLineAsync();

                //TODO read directly from RocksDb with range reads
                for (var y = i; y <= end; y++)
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
                        //Can this even happen?
                        throw new EraException($"Could not find receipts for block {block.ToString(Block.Format.FullHashAndNumber)}");
                    }
                    if (!await builder.Add(block, receipts, cancellation) || y == end)
                    {
                        byte[] root = await builder.Finalize();
                        byte[] checksum = builder.Checksum;
                        builder.Dispose();
                        string rename = Path.Combine(
                                                destinationPath,
                                                EraWriter.Filename(network, i / size, new Hash256(root)));
                        _fileSystem.File.Move(filePath,
                            rename, true);
                        await checksumWriter.WriteAsync(checksum.ToHexString(true));
                        break;
                    }
                    processed++;
                    TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
                    if (elapsed.TotalSeconds > TimeSpan.FromSeconds(10).TotalSeconds)
                    {
                        if (_logger.IsInfo) _logger.Info($"Export progress: {y,10}/{end}  |  elapsed {DateTime.Now.Subtract(startTime):hh\\:mm\\:ss}  |  {processed / elapsed.TotalSeconds,2:0.##} Blk/s");
                        lastProgress = DateTime.Now;
                        processed = 0;
                    }
                }
            }
            if (_logger.IsInfo) _logger.Info($"Finished history export from {start} to {end}");
        }
        catch (EraException e)
        {
            _logger.Error("Import error", e);
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
    public async Task Import(string src, string network, CancellationToken cancellation)
    {
        try
        {
            if (_logger.IsInfo) _logger.Info($"Starting importing blocks");

            DateTime lastProgress = DateTime.Now;
            int fileProcessed = 0;
            var eraFiles = EraReader.GetAllEraFiles(src, network);
            int filesCount = eraFiles.Count();
            DateTime startTime = DateTime.Now;
            int totalCount = 0;

            const int batchSize = 1024;

            using ArrayPoolList<Block> blocks = new ArrayPoolList<Block>(batchSize);
            using ArrayPoolList<TxReceipt[]> receipts = new ArrayPoolList<TxReceipt[]>(batchSize);

            foreach (var era in eraFiles)
            {
                using var eraEnumerator = await EraReader.Create(era);
                int count = 0;
                await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraEnumerator)
                {
                    blocks.Add(b);
                    receipts.Add(r);

                    count++;
                    totalCount++;
                    if (count == batchSize)
                    {
                        ImportBlocks(blocks, receipts, cancellation);
                        blocks.Clear();
                        receipts.Clear();
                        count = 0;
                    }

                    TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
                    if (elapsed.TotalSeconds > TimeSpan.FromSeconds(10).TotalSeconds)
                    {
                        if (_logger.IsInfo)
                        {
                            _logger.Info($"Import progress: {fileProcessed,6}/{filesCount} files  |  elapsed {DateTime.Now.Subtract(startTime):hh\\:mm\\:ss}  |  {totalCount/DateTime.Now.Subtract(startTime).TotalSeconds,2:0.##} Blks/s");
                        }

                        lastProgress = DateTime.Now;
                    }
                }
                if (count > 0)
                {
                    ImportBlocks(blocks, receipts, cancellation);
                }
                fileProcessed++;
            }
            if (_logger.IsInfo) _logger.Info($"Finished importing {totalCount} blocks");

        }
        catch (EraException e)
        {
            _logger.Error("Import error", e);
        }
        catch (TaskCanceledException)
        {
            _logger.Error($"A running import job was cancelled.");
        }
        catch (Exception e)
        {
            _logger.Error("Import error", e);
            throw;
        }
    }
    private void ImportBlocks(ArrayPoolList<Block> blocks, ArrayPoolList<TxReceipt[]> receipts, CancellationToken cancellation)
    {
        if (!blocks.Any())
            return;

        if (blocks.Count != receipts.Count) throw new ArgumentException("There must be an equal amount of blocks and receipts.", nameof(blocks));
        using ArrayPoolList<Block> addedBlocks = new ArrayPoolList<Block>(blocks.Count);

        for (int i = 0; i < blocks.Count; i++)
        {
            Block block = blocks[i];
            TxReceipt[] receipt = receipts[i];
            if (block.IsGenesis)
            {
                continue;
            }

            cancellation.ThrowIfCancellationRequested();

            if (block.IsBodyMissing)
            {
                throw new InvalidOperationException($"A block without a body was passed.");
            }

            if (!_blockValidator.ValidateSuggestedBlock(block))
            {
                throw new EraException($"Era1 archive contains an invalid block {block.ToString(Block.Format.Short)}.");
            }

            ValidateReceipts(block, receipt);

            var addResult = _blockTree.SuggestBlock(block, BlockTreeSuggestOptions.ForceSetAsMain);
            switch (addResult)
            {
                case AddBlockResult.AlreadyKnown:
                    _blockTree.FindLevel
                    return;
                case AddBlockResult.CannotAccept:
                    throw new EraException("Rejected block in Era1 archive");
                case AddBlockResult.UnknownParent:
                    throw new EraException("Unknown parent for block in Era1 archive");
                case AddBlockResult.InvalidBlock:
                    throw new EraException("Invalid block in Era1 archive");
                case AddBlockResult.Added:
                    _receiptStorage.Insert(block, receipt);
                    addedBlocks.Add(block);
                    break;
                default:
                    throw new NotSupportedException($"Not supported value of {nameof(AddBlockResult)} = {addResult}");
            }
        }
        //TODO fast sync moves to main, so the same should apply here?
        _blockTree.UpdateMainChain(addedBlocks, false);
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

    public async Task<bool> VerifyEraFiles(string path)
    {
        var eraFiles = EraReader.GetAllEraFiles(path, "mainnet");

        throw new NotImplementedException();
    }
}
