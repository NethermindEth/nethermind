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
using Nethermind.Era1.Exceptions;

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
    private readonly string _networkName = string.IsNullOrWhiteSpace(eraConfig.NetworkName)
        ? throw new ArgumentException("Cannot be null or whitespace.", nameof(eraConfig.NetworkName))
        : eraConfig.NetworkName.Trim().ToLower();

    private readonly ILogger _logger = logManager.GetClassLogger<EraExporter>();
    private readonly ulong _era1Size = eraConfig.MaxEra1Size;

    public const string AccumulatorFileName = "accumulators.txt";
    public const string ChecksumsFileName = "checksums.txt";

    public Task Export(
        string destinationPath,
        ulong from,
        ulong to,
        CancellationToken cancellation = default)
    {
        if (fileSystem.File.Exists(destinationPath)) throw new ArgumentException($"Destination already exist as a file.", nameof(destinationPath));
        if (to == 0) to = blockTree.Head?.Number ?? 0UL;
        if (to > (blockTree.Head?.Number ?? 0UL)) throw new ArgumentException($"Cannot export to a block after head block {blockTree.Head?.Number ?? 0UL}.");
        if (from > to) throw new ArgumentException($"Start block ({from}) must be before end ({to}) block");

        return DoExport(destinationPath, from, to, cancellation: cancellation);
    }

    private async Task DoExport(
        string destinationPath,
        ulong from,
        ulong to,
        CancellationToken cancellation = default)
    {
        if (_logger.IsInfo) _logger.Info($"Exporting from block {from} to block {to} as Era files to {destinationPath}");
        if (!fileSystem.Directory.Exists(destinationPath))
        {
            fileSystem.Directory.CreateDirectory(destinationPath);
        }

        using ProgressReporter progress = new("Era export", logManager, to - from + 1);
        ulong totalProcessed = 0;

        ulong era1Size = _era1Size;
        ulong startEpoch = from / era1Size;
        ulong epochCount = (to - from + era1Size) / era1Size;

        using ArrayPoolList<ulong> epochIdxs = new((int)epochCount);
        for (ulong i = 0; i < epochCount; i++)
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

        string accumulatorPath = Path.Combine(destinationPath, AccumulatorFileName);
        fileSystem.File.Delete(accumulatorPath);
        await WriteFileAsync(accumulatorPath, accumulators, fileNames, cancellation);

        string checksumPath = Path.Combine(destinationPath, ChecksumsFileName);
        fileSystem.File.Delete(checksumPath);
        await WriteFileAsync(checksumPath, checksums, fileNames, cancellation);

        if (_logger.IsInfo) _logger.Info($"Finished history export from {from} to {to}");

        async Task WriteEpoch(ulong epochIdx)
        {
            // Yes, it offset a bit so a block that is at the end of an epoch would be at the start of another epoch
            // if the start is not a multiple of _era1Size. This seems to match geth's behaviour.
            ulong epoch = startEpoch + epochIdx;
            ulong startingIndex = from + epochIdx * era1Size;

            string filePath = Path.Combine(
                destinationPath,
                EraPathUtils.Filename(_networkName, epoch, Keccak.Zero));

            ValueHash256 accumulator;
            ValueHash256 sha256;

            // Scoped using so the writer is disposed before File.Move — Windows locks open files.
            using (EraWriter eraWriter = new(fileSystem.File.Create(filePath), specProvider))
            {
                for (ulong y = startingIndex; y < startingIndex + era1Size && y <= to; y++)
                {
                    Block? block = blockTree.FindBlock(y, BlockTreeLookupOptions.DoNotCreateLevelIfMissing)
                        ?? throw new EraException($"Could not find a block with number {y}.");

                    TxReceipt[]? receipts = receiptStorage.Get(block, true, false);
                    if (receipts is null || (block.Header.ReceiptsRoot != Keccak.EmptyTreeHash && receipts.Length == 0))
                    {
                        throw new EraException($"Could not find receipts for block {block.ToString(Block.Format.FullHashAndNumber)} {receiptStorage.GetHashCode()}");
                    }

                    if (block.TotalDifficulty is null)
                    {
                        throw new EraException($"Block {block.ToString(Block.Format.FullHashAndNumber)} does not have total difficulty specified");
                    }

                    await eraWriter.Add(block, receipts, cancellation);
                    progress.Update(Interlocked.Increment(ref totalProcessed));
                }

                (accumulator, sha256) = await eraWriter.Finalize(cancellation);
            }

            accumulators[(int)epochIdx] = accumulator;
            checksums[(int)epochIdx] = sha256;
            fileNames[(int)epochIdx] = Path.GetFileName(filePath);
            string rename = Path.Combine(
                destinationPath,
                EraPathUtils.Filename(_networkName, epoch, new Hash256(accumulator)));
            // Retry to handle transient file locks on Windows (e.g. antivirus scanning).
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    fileSystem.File.Move(filePath, rename, true);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    await Task.Delay(100 * (attempt + 1), cancellation);
                }
            }
        }
    }

    private async Task WriteFileAsync(string path, ArrayPoolList<ValueHash256> hashes, ArrayPoolList<string> fileNames, CancellationToken cancellationToken)
    {
        await using FileSystemStream stream = fileSystem.FileStream.New(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using StreamWriter writer = new(stream);

        for (int i = 0; i < hashes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync(hashes[i].ToString());
            await writer.WriteAsync(' ');
            await writer.WriteLineAsync(fileNames[i]);
        }
    }
}
