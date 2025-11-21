// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.EraE;

public class EraExporter(
    IFileSystem fileSystem,
    IBlockTree blockTree,
    IReceiptStorage receiptStorage,
    ISpecProvider specProvider,
    IEraEConfig eraConfig,
    ILogManager logManager)
    : Era1.EraExporter(fileSystem,
        blockTree,
        receiptStorage,
        specProvider,
        new Era1.EraConfig
        {
            MaxEra1Size = eraConfig.MaxEraESize,
            NetworkName = eraConfig.NetworkName,
            Concurrency = eraConfig.Concurrency,
            ImportBlocksBufferSize = eraConfig.ImportBlocksBufferSize,
            TrustedAccumulatorFile = eraConfig.TrustedAccumulatorFile,
            From = eraConfig.From,
            To = eraConfig.To,
            ExportDirectory = eraConfig.ExportDirectory,
            ImportDirectory = eraConfig.ImportDirectory
        },
        logManager)
{
    private readonly IFileSystem fileSystem = fileSystem;
    private readonly IBlockTree blockTree = blockTree;
    private readonly IReceiptStorage receiptStorage = receiptStorage;
    private readonly ISpecProvider specProvider = specProvider;
    private readonly IEraEConfig eraConfig = eraConfig;
    private readonly ILogManager logManager = logManager;

    private readonly int _eraSize = eraConfig.MaxEraESize;

    protected override EraWriter GetWriter(string filePath, ISpecProvider specProvider)
    {
        return new EraWriter(filePath, specProvider);
    }

    protected override async Task DoExport(
        string destinationPath,
        long from,
        long to,
        CancellationToken cancellation = default)
    {
        if (_logger.IsInfo) _logger.Info($"Exporting from block {from} to block {to} as EraE files to {destinationPath}");
        if (!fileSystem.Directory.Exists(destinationPath))
        {
            fileSystem.Directory.CreateDirectory(destinationPath);
        }

        ProgressLogger progress = new ProgressLogger("EraE export", logManager);
        progress.Reset(0, to - from + 1);
        int totalProcessed = 0;

        long startEpoch = from / _eraSize;
        long epochCount = (long)Math.Ceiling((to - from + 1) / (decimal)_eraSize);
        using ArrayPoolList<long> epochIdxs = new((int)epochCount);
        for (long i = 0; i < epochCount; i++)
        {
            epochIdxs.Add(i);
        }

        using ArrayPoolList<ValueHash256> accumulators = new((int)epochCount, (int)epochCount);
        using ArrayPoolList<ValueHash256> checksums = new((int)epochCount, (int)epochCount);
        using ArrayPoolList<string> fileNames = new((int)epochCount, (int)epochCount);

        await Parallel.ForEachAsync(epochIdxs, new ParallelOptions
        {
            MaxDegreeOfParallelism = eraConfig.Concurrency == 0 ? Environment.ProcessorCount : eraConfig.Concurrency,
            CancellationToken = cancellation
        },
        async (epochIdx, cancel)
            =>
        {
            await WriteEpoch(epochIdx);
        });

        string checksumPath = Path.Combine(destinationPath, ChecksumsFileName);
        fileSystem.File.Delete(checksumPath);
        await WriteFileAsync(checksumPath, checksums, fileNames, cancellation);

        progress.LogProgress();

        if (_logger.IsInfo) _logger.Info($"Finished history export from {from} to {to}");

        async Task WriteEpoch(long epochIdx)
        {
            // Yes, it offset a bit so a block that is at the end of an epoch would be at the start of another epoch
            // if the start is not of module _era1Size. This seems to match geth's behaviour.
            long epoch = startEpoch + epochIdx;
            long startingIndex = from + epochIdx * _eraSize;

            string filePath = Path.Combine(destinationPath, $"{_networkName}-{epoch:D5}.erae");
            EraWriter eraWriter = GetWriter(filePath, specProvider);

            for (var y = startingIndex; y < startingIndex + _eraSize && y <= to; y++)
            {
                Block? block = blockTree.FindBlock(y, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
                if (block is null)
                {
                    throw new Era1.EraException($"Could not find a block with number {y}.");
                }

                TxReceipt[]? receipts = receiptStorage.Get(block, true, false);
                if (receipts is null || (block.Header.ReceiptsRoot != Keccak.EmptyTreeHash && receipts.Length == 0))
                {
                    throw new Era1.EraException($"Could not find receipts for block {block.ToString(Block.Format.FullHashAndNumber)} {receiptStorage.GetHashCode()}");
                }

                if (block.TotalDifficulty is null)
                {
                    throw new Era1.EraException($"Block {block.ToString(Block.Format.FullHashAndNumber)} does  not have total difficulty specified");
                }

                await eraWriter.Add(block, receipts, cancellation);

                bool shouldLog = (Interlocked.Increment(ref totalProcessed) % 10000) == 0;
                if (shouldLog)
                {
                    progress.Update(totalProcessed);
                    progress.LogProgress();
                }
            }

            ValueHash256 sha256 = await eraWriter.Finalize(cancellation);
            checksums[(int)epochIdx] = sha256;
            fileNames[(int)epochIdx] = Path.GetFileName(filePath);
        }
    }
}
