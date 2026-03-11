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
    : IEraExporter
{
    private readonly string _networkName = string.IsNullOrWhiteSpace(eraConfig.NetworkName)
        ? throw new ArgumentException("NetworkName cannot be null or whitespace.", nameof(eraConfig))
        : eraConfig.NetworkName.Trim().ToLower();

    private readonly ILogger _logger = logManager.GetClassLogger<EraExporter>();
    private readonly int _eraSize = eraConfig.MaxEraSize;

    public const string AccumulatorFileName = "accumulators.txt";
    public const string ChecksumsFileName = "checksums.txt";

    public Task Export(string destinationPath, long from, long to, CancellationToken cancellation = default)
    {
        if (fileSystem.File.Exists(destinationPath))
            throw new ArgumentException("Destination already exists as a file.", nameof(destinationPath));
        if (to == 0) to = blockTree.Head?.Number ?? 0;
        if (to > (blockTree.Head?.Number ?? 0))
            throw new ArgumentException($"Cannot export beyond head block {blockTree.Head?.Number ?? 0}.");
        if (from > to)
            throw new ArgumentException($"Start block ({from}) must not be after end block ({to}).");

        return DoExport(destinationPath, from, to, cancellation);
    }

    private async Task DoExport(string destinationPath, long from, long to, CancellationToken cancellation)
    {
        if (_logger.IsInfo) _logger.Info($"Exporting EraE blocks {from}–{to} to {destinationPath}");

        if (!fileSystem.Directory.Exists(destinationPath))
            fileSystem.Directory.CreateDirectory(destinationPath);

        ProgressLogger progress = new("EraE export", logManager);
        progress.Reset(0, to - from + 1);
        int totalProcessed = 0;

        long startEpoch = from / _eraSize;
        long epochCount = (long)Math.Ceiling((to - from + 1) / (decimal)_eraSize);

        using ArrayPoolList<long> epochIdxs = new((int)epochCount);
        for (long i = 0; i < epochCount; i++) epochIdxs.Add(i);

        using ArrayPoolList<ValueHash256> accumulators = new((int)epochCount, (int)epochCount);
        using ArrayPoolList<ValueHash256> checksums = new((int)epochCount, (int)epochCount);
        using ArrayPoolList<string> fileNames = new((int)epochCount, (int)epochCount);

        await Parallel.ForEachAsync(epochIdxs, new ParallelOptions
        {
            MaxDegreeOfParallelism = eraConfig.Concurrency == 0 ? Environment.ProcessorCount : eraConfig.Concurrency,
            CancellationToken = cancellation
        },
        async (epochIdx, cancel) => await WriteEpoch(epochIdx, cancel));

        string accumulatorPath = Path.Combine(destinationPath, AccumulatorFileName);
        fileSystem.File.Delete(accumulatorPath);
        await WriteHashFile(accumulatorPath, accumulators, fileNames, cancellation);

        string checksumPath = Path.Combine(destinationPath, ChecksumsFileName);
        fileSystem.File.Delete(checksumPath);
        await WriteHashFile(checksumPath, checksums, fileNames, cancellation);

        progress.LogProgress();
        if (_logger.IsInfo) _logger.Info($"Finished EraE export from {from} to {to}");

        async Task WriteEpoch(long epochIdx, CancellationToken cancel)
        {
            long epoch = startEpoch + epochIdx;
            long startingIndex = from + epochIdx * _eraSize;

            string filePath = Path.Combine(
                destinationPath,
                EraPathUtils.Filename(_networkName, epoch, Keccak.Zero));

            ValueHash256 accumulator;
            ValueHash256 sha256;

            using (EraWriter eraWriter = new(fileSystem.File.Create(filePath), specProvider))
            {
                for (long y = startingIndex; y < startingIndex + _eraSize && y <= to; y++)
                {
                    Block? block = blockTree.FindBlock(y, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
                    if (block is null)
                        throw new EraException($"Could not find block {y}.");

                    TxReceipt[]? receipts = receiptStorage.Get(block, true, false);
                    if (receipts is null || (block.Header.ReceiptsRoot != Keccak.EmptyTreeHash && receipts.Length == 0))
                        throw new EraException($"Could not find receipts for block {block.ToString(Block.Format.FullHashAndNumber)}.");

                    await eraWriter.Add(block, receipts, cancel);

                    if ((Interlocked.Increment(ref totalProcessed) % 10000) == 0)
                    {
                        progress.Update(totalProcessed);
                        progress.LogProgress();
                    }
                }

                (accumulator, sha256) = await eraWriter.Finalize(cancel);
            }

            accumulators[(int)epochIdx] = accumulator;
            checksums[(int)epochIdx] = sha256;
            string finalName = EraPathUtils.Filename(_networkName, epoch, new Hash256(accumulator));
            fileNames[(int)epochIdx] = finalName;

            string rename = Path.Combine(destinationPath, finalName);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    fileSystem.File.Move(filePath, rename, true);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    await Task.Delay(100 * (attempt + 1), cancel);
                }
            }
        }
    }

    private async Task WriteHashFile(string path, ArrayPoolList<ValueHash256> hashes, ArrayPoolList<string> fileNames, CancellationToken cancellation)
    {
        await using FileSystemStream stream = fileSystem.FileStream.New(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using StreamWriter writer = new(stream);

        for (int i = 0; i < hashes.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            await writer.WriteAsync(hashes[i].ToString());
            await writer.WriteAsync(' ');
            await writer.WriteLineAsync(fileNames[i]);
        }
    }
}
