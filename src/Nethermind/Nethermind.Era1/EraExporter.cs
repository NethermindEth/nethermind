// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
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
    private readonly int _era1Size;

    public string NetworkName => _networkName;

    public const string AccumulatorFileName = "accumulators.txt";

    public EraExporter(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        IEraConfig eraConfig,
        ILogManager logManager,
        [KeyFilter(EraComponentKeys.NetworkName)] string networkName)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _era1Size = eraConfig.MaxEra1Size;
        if (string.IsNullOrWhiteSpace(networkName)) throw new ArgumentException("Cannot be null or whitespace.", nameof(specProvider));
        _logger = logManager.GetClassLogger<EraExporter>();
        _networkName = networkName.Trim().ToLower();
    }

    public async Task Export(
        string destinationPath,
        long start,
        long end,
        bool createAccumulator = true,
        CancellationToken cancellation = default)
    {
        if (destinationPath is null) throw new ArgumentNullException(nameof(destinationPath));
        if (_fileSystem.File.Exists(destinationPath)) throw new ArgumentException(nameof(destinationPath), $"Cannot be a file.");
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

        long epochCount = (long)Math.Ceiling((end - start + 1) / (decimal)_era1Size);
        byte[][] eraRoots = new byte[epochCount][];

        List<long> epochIdxs = new List<long>();
        for (long i = 0; i < epochCount; i++)
        {
            epochIdxs.Add(i);
        }

        await Parallel.ForEachAsync(epochIdxs, cancellation, async (epochIdx, cancel) =>
        {
            await WriteEpoch(epochIdx);
        });

        if (createAccumulator)
        {
            string accumulatorPath = Path.Combine(destinationPath, AccumulatorFileName);
            await new EraStore(destinationPath, null, _specProvider, _networkName, _fileSystem, _era1Size).CreateAccumulatorFile(accumulatorPath, cancellation);
        }

        LogExportProgress(
            end - start,
            totalProcessed,
            processedSinceLast,
            txProcessedSinceLast,
            DateTime.Now.Subtract(lastProgress),
            DateTime.Now.Subtract(startTime));

        if (_logger.IsInfo) _logger.Info("Export completed");

        async Task WriteEpoch(long epochIdx)
        {
            // Hmm.. if it does not start at the boundary, then it all offset?
            // What would be the expected way to encode this?
            long startingIndex = start + epochIdx * _era1Size;
            string filePath = Path.Combine(
                destinationPath,
                EraWriter.Filename(_networkName, startingIndex / _era1Size, Keccak.Zero));

            using EraWriter builder = new EraWriter(_fileSystem.File.Create(filePath), _specProvider);

            //TODO read directly from RocksDb with range reads
            for (var y = startingIndex; y < startingIndex + _era1Size && y <= end; y++)
            {
                Block? block = _blockTree.FindBlock(y, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
                if (block is null)
                {
                    throw new EraException($"Could not find a block with number {y}.");
                }

                // TODO: Check if recover: false is safe
                TxReceipt[]? receipts = _receiptStorage.Get(block, recover: false);
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

                await builder.Add(block, receipts, cancellation);

                bool shouldLog = (Interlocked.Increment(ref totalProcessed) % 10000) == 0;
                Interlocked.Increment(ref processedSinceLast);
                Interlocked.Add(ref txProcessedSinceLast, block.Transactions.Length);
                if (shouldLog)
                {
                    LogExportProgress(
                        end - start,
                        totalProcessed,
                        processedSinceLast,
                        txProcessedSinceLast,
                        DateTime.Now.Subtract(lastProgress),
                        DateTime.Now.Subtract(startTime));
                    lastProgress = DateTime.Now;
                    processedSinceLast = 0;
                    txProcessedSinceLast = 0;
                }
            }

            byte[] root = await builder.Finalize(cancellation);
            string rename = Path.Combine(
                destinationPath,
                EraWriter.Filename(_networkName, startingIndex / _era1Size, new Hash256(root)));
            _fileSystem.File.Move(
                filePath,
                rename, true);

            eraRoots[epochIdx] = root;
            builder.Dispose();
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
