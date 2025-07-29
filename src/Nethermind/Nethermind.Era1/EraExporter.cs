// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Era1;

public class EraExporter(
    IFileSystem fileSystem,
    IBlockTree blockTree,
    IReceiptStorage receiptStorage,
    ISpecProvider specProvider,
    IEraConfig eraConfig,
    ILogManager logManager)
    : IEraExporter
{
    private readonly string _networkName = (string.IsNullOrWhiteSpace(eraConfig.NetworkName)) ? throw new ArgumentException("Cannot be null or whitespace.", nameof(eraConfig.NetworkName)) : eraConfig.NetworkName.Trim().ToLower();
    private readonly ILogger _logger = logManager.GetClassLogger<EraExporter>();
    private readonly int _era1Size = eraConfig.MaxEra1Size;

    public const string AccumulatorFileName = "accumulators.txt";
    public const string ChecksumsFileName = "checksums.txt";

    public Task Export(
        string destinationPath,
        long from,
        long to,
        CancellationToken cancellation = default)
    {
        if (fileSystem.File.Exists(destinationPath)) throw new ArgumentException($"Destination already exist as a file.", nameof(destinationPath));
        if (to == 0) to = blockTree.Head?.Number ?? 0;
        if (to > (blockTree.Head?.Number ?? 0)) throw new ArgumentException($"Cannot export to a block after head block {blockTree.Head?.Number ?? 0}.");
        if (from > to) throw new ArgumentException($"Start block ({from}) must be before end ({to}) block");

        return DoExport(destinationPath, from, to, cancellation: cancellation);
    }

    private async Task DoExport(
        string destinationPath,
        long from,
        long to,
        CancellationToken cancellation = default)
    {
        if (_logger.IsInfo) _logger.Info($"Exporting from block {from} to block {to} as Era files to {destinationPath}");
        if (!fileSystem.Directory.Exists(destinationPath))
        {
            fileSystem.Directory.CreateDirectory(destinationPath);
        }

        ProgressLogger progress = new ProgressLogger("Era export", logManager);
        progress.Reset(0, to - from + 1);
        int totalProcessed = 0;

        long startEpoch = from / _era1Size;
        long epochCount = (long)Math.Ceiling((to - from + 1) / (decimal)_era1Size);
        using ArrayPoolList<long> epochIdxs = new((int)epochCount);
        for (long i = 0; i < epochCount; i++)
        {
            epochIdxs.Add(i);
        }

        using ArrayPoolList<ValueHash256> accumulators = new((int)epochCount, (int)epochCount);
        using ArrayPoolList<ValueHash256> checksums = new((int)epochCount, (int)epochCount);

        await Parallel.ForEachAsync(epochIdxs, new ParallelOptions()
        {
            MaxDegreeOfParallelism = (eraConfig.Concurrency == 0 ? Environment.ProcessorCount : eraConfig.Concurrency),
            CancellationToken = cancellation
        },
        async (epochIdx, cancel) =>
        {
            await WriteEpoch(epochIdx);
        });

        string accumulatorPath = Path.Combine(destinationPath, AccumulatorFileName);
        fileSystem.File.Delete(accumulatorPath);
        await fileSystem.File.WriteAllLinesAsync(accumulatorPath, accumulators.Select((v) => v.ToString()), cancellation);

        string checksumPath = Path.Combine(destinationPath, ChecksumsFileName);
        fileSystem.File.Delete(checksumPath);
        await fileSystem.File.WriteAllLinesAsync(checksumPath, checksums.Select((v) => v.ToString()), cancellation);

        progress.LogProgress();

        if (_logger.IsInfo) _logger.Info($"Finished history export from {from} to {to}");

        async Task WriteEpoch(long epochIdx)
        {
            // Yes, it offset a bit so a block that is at the end of an epoch would be at the start of another epoch
            // if the start is not of module _era1Size. This seems to match geth's behaviour.
            long epoch = startEpoch + epochIdx;
            long startingIndex = from + epochIdx * _era1Size;

            string filePath = Path.Combine(
                destinationPath,
                EraPathUtils.Filename(_networkName, epoch, Keccak.Zero));

            using EraWriter eraWriter = new EraWriter(fileSystem.File.Create(filePath), specProvider);

            for (var y = startingIndex; y < startingIndex + _era1Size && y <= to; y++)
            {
                Block? block = blockTree.FindBlock(y, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
                if (block is null)
                {
                    throw new EraException($"Could not find a block with number {y}.");
                }

                TxReceipt[]? receipts = receiptStorage.Get(block, true, false);
                if (receipts is null || (block.Header.ReceiptsRoot != Keccak.EmptyTreeHash && receipts.Length == 0))
                {
                    throw new EraException($"Could not find receipts for block {block.ToString(Block.Format.FullHashAndNumber)} {receiptStorage.GetHashCode()}");
                }

                if (block.TotalDifficulty is null)
                {
                    throw new EraException($"Block {block.ToString(Block.Format.FullHashAndNumber)} does  not have total difficulty specified");
                }

                await eraWriter.Add(block, receipts, cancellation);

                bool shouldLog = (Interlocked.Increment(ref totalProcessed) % 10000) == 0;
                if (shouldLog)
                {
                    progress.Update(totalProcessed);
                    progress.LogProgress();
                }
            }

            (ValueHash256 accumulator, ValueHash256 sha256) = await eraWriter.Finalize(cancellation);
            accumulators[(int)epochIdx] = accumulator;
            checksums[(int)epochIdx] = sha256;

            string rename = Path.Combine(
                destinationPath,
                EraPathUtils.Filename(_networkName, epoch, new Hash256(accumulator)));
            fileSystem.File.Move(
                filePath,
                rename, true);
        }
    }
}
