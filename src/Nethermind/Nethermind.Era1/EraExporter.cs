// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Era1;
public class EraExporter : IEraExporter
{
    private const int MergeBlock = 15537393;
    private readonly IFileSystem _fileSystem;
    private readonly IBlockTree _blockTree;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ISpecProvider _specProvider;
    private readonly string _networkName;
    private readonly ILogger _logger;

    public string NetworkName => _networkName;

    public const string AccumulatorFileName = "accumulators.txt";

    public EraExporter(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        ILogManager logManager,
        [KeyFilter(EraComponentKeys.NetworkName)] string networkName)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        if (string.IsNullOrWhiteSpace(networkName)) throw new ArgumentException("Cannot be null or whitespace.", nameof(specProvider));
        _logger = logManager.GetClassLogger<EraExporter>();
        _networkName = networkName.Trim().ToLower();
    }

    public async Task Export(
        string destinationPath,
        long start,
        long end,
        int size = EraWriter.MaxEra1Size,
        bool createAccumulator = true,
        CancellationToken cancellation = default)
    {
        if (destinationPath is null) throw new ArgumentNullException(nameof(destinationPath));
        if (_fileSystem.File.Exists(destinationPath)) throw new ArgumentException(nameof(destinationPath), $"Cannot be a file.");
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(size), size, $"Must be greater than 0.");
        if (size > EraWriter.MaxEra1Size) throw new ArgumentOutOfRangeException(nameof(size), size, $"Cannot be greater than {EraWriter.MaxEra1Size}.");

        if (_logger.IsInfo) _logger.Info($"Exporting block {start} to block {end} as Era files to {destinationPath}");

        if (!_fileSystem.Directory.Exists(destinationPath))
        {
            //TODO look into permission settings - should it be set?
            _fileSystem.Directory.CreateDirectory(destinationPath);
        }

        DateTime startTime = DateTime.Now;
        DateTime lastProgress = DateTime.Now;

        int totalProcessed = 0;
        int processedSinceLast = 0;
        int txProcessedSinceLast = 0;

        List<byte[]> eraRoots = new();
        for (long i = start; i <= end; i += size)
        {
            string filePath = Path.Combine(
               destinationPath,
               EraWriter.Filename(_networkName, i / size, Keccak.Zero));
            using EraWriter? builder = EraWriter.Create(_fileSystem.File.Create(filePath), _specProvider);

            //TODO read directly from RocksDb with range reads
            for (var y = i; y <= i + size; y++)
            {
                Block? block = _blockTree.FindBlock(y, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
                if (block is null)
                {
                    throw new EraException($"Could not find a block with number {y}.");
                }

                TxReceipt[]? receipts = _receiptStorage.Get(block);
                if (receipts is null)
                {
                    // Can this even happen?
                    // Well yea... it happens a lot unfortunately
                    throw new EraException($"Could not find receipts for block {block.ToString(Block.Format.FullHashAndNumber)}");
                }

                // TODO: Check why
                // UInt256 td = block.TotalDifficulty ?? _blockTree.GetInfo(block.Number, block.Hash).Info?.TotalDifficulty ?? block.Difficulty;
                if (block.TotalDifficulty is null)
                {
                    throw new EraException($"Block does not have total difficulty specified");
                }

                if (!await builder.Add(block, receipts, cancellation) || y == i + size || y == end)
                {
                    byte[] root = await builder.Finalize();
                    builder.Dispose();
                    string rename = Path.Combine(
                                            destinationPath,
                                            EraWriter.Filename(_networkName, i / size, new Hash256(root)));
                    _fileSystem.File.Move(
                        filePath,
                        rename, true);
                    eraRoots.Add(root);
                    break;
                }
                totalProcessed++;
                txProcessedSinceLast += block.Transactions.Length;
                processedSinceLast++;
                TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
                if (elapsed.TotalSeconds > TimeSpan.FromSeconds(10).TotalSeconds)
                {
                    LogExportProgress(
                        end - start,
                        totalProcessed,
                        processedSinceLast,
                        txProcessedSinceLast,
                        elapsed,
                        DateTime.Now.Subtract(startTime));
                    lastProgress = DateTime.Now;
                    processedSinceLast = 0;
                    txProcessedSinceLast = 0;
                }
            }
        }

        if (createAccumulator)
        {
            string accumulatorPath = Path.Combine(destinationPath, AccumulatorFileName);
            await new EraStore(destinationPath, _networkName, _fileSystem).CreateAccumulatorFile(accumulatorPath, cancellation);
        }
    }

    private void LogExportProgress(
        long totalBlocks,
        long totalBlocksProcessed,
        long blocksProcessedSinceLast,
        long txProcessedSinceLast,
        TimeSpan sinceLast,
        TimeSpan elapsed)
    {
        if (_logger.IsInfo)
            _logger.Info($"Export progress: {totalBlocksProcessed,10}/{totalBlocks} blocks  |  elapsed {elapsed:hh\\:mm\\:ss}  |  {blocksProcessedSinceLast / sinceLast.TotalSeconds,10:0.00} Blk/s  |  {txProcessedSinceLast / sinceLast.TotalSeconds,10:0.00} tx/s");
    }
}
