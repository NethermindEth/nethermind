// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Era1;
public class EraExporter : IEraExporter
{
    private readonly IFileSystem _fileSystem;
    private readonly IBlockTree _blockTree;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ISpecProvider _specProvider;
    private readonly string _networkName;
    private readonly ILogger _logger;
    private readonly int _era1Size;

    public const string AccumulatorFileName = "accumulators.txt";
    public const string ChecksumsFileName = "checksums.txt";

    public EraExporter(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        IEraConfig eraConfig,
        ILogManager logManager)
    {
        _fileSystem = fileSystem;
        _blockTree = blockTree;
        _receiptStorage = receiptStorage;
        _specProvider = specProvider;
        _era1Size = eraConfig.MaxEra1Size;
        string? networkName = eraConfig.NetworkName;
        if (string.IsNullOrWhiteSpace(networkName)) throw new ArgumentException("Cannot be null or whitespace.", nameof(specProvider));
        _logger = logManager.GetClassLogger<EraExporter>();
        _networkName = networkName.Trim().ToLower();
    }

    public Task Export(
        string destinationPath,
        long from,
        long to,
        CancellationToken cancellation = default)
    {
        if (destinationPath is null) throw new ArgumentNullException(nameof(destinationPath));
        if (_fileSystem.File.Exists(destinationPath)) throw new ArgumentException($"Cannot be a file.", nameof(destinationPath));
        if (to == 0) to = _blockTree.Head?.Number ?? 0;
        if (to > (_blockTree.Head?.Number ?? 0)) throw new ArgumentException($"Cannot export to a block after head block {_blockTree.Head?.Number ?? 0}.", nameof(to));
        if (from > to) throw new ArgumentException("Start must be before end block", nameof(from));

        return DoExport(destinationPath, from, to, cancellation: cancellation);
    }

    private async Task DoExport(
        string destinationPath,
        long from,
        long to,
        CancellationToken cancellation = default)
    {
        if (_logger.IsInfo) _logger.Info($"Exporting block {from} to block {to} as Era files to {destinationPath}");
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

        long epochCount = (long)Math.Ceiling((to - from + 1) / (decimal)_era1Size);

        long startEpoch = from / _era1Size;

        using ArrayPoolList<long> epochIdxs = new((int)epochCount);
        for (long i = 0; i < epochCount; i++)
        {
            epochIdxs.Add(i);
        }

        using ArrayPoolList<ValueHash256> accumulators = new((int)epochCount, (int)epochCount);
        using ArrayPoolList<ValueHash256> checksums = new((int)epochCount, (int)epochCount);

        await Parallel.ForEachAsync(epochIdxs, cancellation, async (epochIdx, cancel) =>
        {
            await WriteEpoch(epochIdx);
        });

        string accumulatorPath = Path.Combine(destinationPath, AccumulatorFileName);
        _fileSystem.File.Delete(accumulatorPath);
        await _fileSystem.File.WriteAllLinesAsync(accumulatorPath, accumulators.Select((v) => v.ToString()), cancellation);

        string checksumPath = Path.Combine(destinationPath, ChecksumsFileName);
        _fileSystem.File.Delete(checksumPath);
        await _fileSystem.File.WriteAllLinesAsync(checksumPath, checksums.Select((v) => v.ToString()), cancellation);

        LogExportProgress(
            to - from,
            totalProcessed,
            processedSinceLast,
            txProcessedSinceLast,
            DateTime.Now.Subtract(lastProgress),
            DateTime.Now.Subtract(startTime));

        if (_logger.IsInfo) _logger.Info($"Finished history export from {from} to {to}");

        async Task WriteEpoch(long epochIdx)
        {
            // Yes, it offset a bit so a block that is at the end of an epoch would be at the start of another epoch
            // if the start is not of module _era1Size. This seems to match geth's behaviour.
            long epoch = startEpoch + epochIdx;
            long startingIndex = from + epochIdx * _era1Size;

            string filePath = Path.Combine(
                destinationPath,
                EraWriter.Filename(_networkName, epoch, Keccak.Zero));

            using EraWriter builder = new EraWriter(_fileSystem.File.Create(filePath), _specProvider);

            for (var y = startingIndex; y < startingIndex + _era1Size && y <= to; y++)
            {
                Block? block = _blockTree.FindBlock(y, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
                if (block is null)
                {
                    throw new EraException($"Could not find a block with number {y}.");
                }

                TxReceipt[]? receipts = _receiptStorage.Get(block, true, false);
                if (receipts is null || (block.Header.ReceiptsRoot != Keccak.EmptyTreeHash && receipts.Length == 0))
                {
                    throw new EraException($"Could not find receipts for block {block.ToString(Block.Format.FullHashAndNumber)} {_receiptStorage.GetHashCode()}");
                }

                if (block.TotalDifficulty is null)
                {
                    throw new EraException($"Block {block.ToString(Block.Format.FullHashAndNumber)} does  not have total difficulty specified");
                }

                await builder.Add(block, receipts, cancellation);

                bool shouldLog = (Interlocked.Increment(ref totalProcessed) % 10000) == 0;
                Interlocked.Increment(ref processedSinceLast);
                Interlocked.Add(ref txProcessedSinceLast, block.Transactions.Length);
                if (shouldLog)
                {
                    LogExportProgress(
                        to - from,
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

            (ValueHash256 accumulator, ValueHash256 sha256) = await builder.Finalize(cancellation);
            accumulators[(int)epochIdx] = accumulator;
            checksums[(int)epochIdx] = sha256;

            string rename = Path.Combine(
                destinationPath,
                EraWriter.Filename(_networkName, epoch, new Hash256(accumulator)));
            _fileSystem.File.Move(
                filePath,
                rename, true);

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
