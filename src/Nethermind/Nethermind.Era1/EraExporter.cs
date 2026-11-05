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

        string accumulatorPath = Path.Combine(destinationPath, AccumulatorFileName);
        string checksumPath = Path.Combine(destinationPath, ChecksumsFileName);
        Dictionary<long, string> earlierFiles = [];
        long firstEarlierEpoch = 0;
        foreach (string file in EraPathUtils.GetAllEraFiles(destinationPath, _networkName, fileSystem))
        {
            if (!EraPathUtils.TryGetEpoch(file, out long epoch)) continue;
            if (earlierFiles.Count == 0 || epoch < firstEarlierEpoch) firstEarlierEpoch = epoch;
            earlierFiles[epoch] = Path.GetFileName(file);
        }
        Dictionary<ulong, List<(string Path, bool IsPlaceholder)>> earlierEpochFiles = [];
        foreach (string file in fileSystem.Directory.EnumerateFiles(destinationPath).Order(StringComparer.Ordinal))
        {
            if (!EraPathUtils.TryParseEpochFile(file, _networkName, out ulong epoch, out bool isPlaceholder) || epoch < startEpoch || epoch >= startEpoch + epochCount)
                continue;

            if (!earlierEpochFiles.TryGetValue(epoch, out List<(string Path, bool IsPlaceholder)>? files))
                earlierEpochFiles[epoch] = files = [];
            files.Add((file, isPlaceholder));
        }
        SortedDictionary<long, (ValueHash256 Hash, string FileName)> accumulatorEntries = ReadEarlierEntries(accumulatorPath);
        SortedDictionary<long, (ValueHash256 Hash, string FileName)> checksumEntries = ReadEarlierEntries(checksumPath);

        // Check every epoch before writing any, so a rejected epoch leaves the directory as it was.
        for (ulong epochIdx = 0; epochIdx < epochCount; epochIdx++)
        {
            (ulong startingIndex, ulong endingIndex) = GetWriteRange(epochIdx);
            EnsureNoBlocksLost(earlierEpochFiles.GetValueOrDefault(startEpoch + epochIdx), startEpoch + epochIdx, startingIndex, endingIndex, from, to, era1Size);
        }

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

        for (int i = 0; i < fileNames.Count; i++)
        {
            long epoch = (long)startEpoch + i;
            accumulatorEntries[epoch] = (accumulators[i], fileNames[i]);
            checksumEntries[epoch] = (checksums[i], fileNames[i]);
        }

        fileSystem.File.Delete(accumulatorPath);
        await WriteFileAsync(accumulatorPath, accumulatorEntries, cancellation);

        fileSystem.File.Delete(checksumPath);
        await WriteFileAsync(checksumPath, checksumEntries, cancellation);

        // Only now, with every epoch exported and the manifests naming the new files, delete the files they replace.
        for (int i = 0; i < fileNames.Count; i++)
        {
            if (earlierEpochFiles.TryGetValue(startEpoch + (ulong)i, out List<(string Path, bool IsPlaceholder)>? files))
                DeleteSupersededFiles(files.Select(static f => f.Path), Path.Combine(destinationPath, fileNames[i]));
        }

        if (_logger.IsInfo) _logger.Info($"Finished history export from {from} to {to}");

        // Keeps the entries of an earlier export into this directory whose epochs still have an era file,
        // so the rewritten manifest covers every epoch in the directory.
        SortedDictionary<long, (ValueHash256 Hash, string FileName)> ReadEarlierEntries(string path)
        {
            SortedDictionary<long, (ValueHash256 Hash, string FileName)> entries = [];
            if (!fileSystem.File.Exists(path)) return entries;

            foreach ((long epoch, ValueHash256 hash) in EraPathUtils.ParseManifest(fileSystem.File.ReadAllLines(path), firstEarlierEpoch))
            {
                if (earlierFiles.TryGetValue(epoch, out string? fileName))
                    entries[epoch] = (hash, fileName);
            }

            return entries;
        }

        // Yes, it offset a bit so a block that is at the end of an epoch would be at the start of another epoch
        // if the start is not a multiple of _era1Size. This seems to match geth's behaviour.
        (ulong StartingIndex, ulong EndingIndex) GetWriteRange(ulong epochIdx)
        {
            ulong startingIndex = from + epochIdx * era1Size;
            return (startingIndex, Math.Min(startingIndex + era1Size - 1, to));
        }

        async Task WriteEpoch(ulong epochIdx)
        {
            ulong epoch = startEpoch + epochIdx;
            (ulong startingIndex, ulong endingIndex) = GetWriteRange(epochIdx);

            string filePath = Path.Combine(
                destinationPath,
                EraPathUtils.Filename(_networkName, epoch, Keccak.Zero));

            ValueHash256 accumulator;
            ValueHash256 sha256;

            // Scoped using so the writer is disposed before File.Move — Windows locks open files.
            using (EraWriter eraWriter = new(fileSystem.File.Create(filePath), specProvider))
            {
                for (ulong y = startingIndex; y <= endingIndex; y++)
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
            string rename = Path.Combine(
                destinationPath,
                EraPathUtils.Filename(_networkName, epoch, new Hash256(accumulator)));
            fileNames[(int)epochIdx] = Path.GetFileName(rename);
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

    /// <summary>
    /// A new file replaces the earlier files of the epoch, so it must hold every block they hold.
    /// </summary>
    private static void EnsureNoBlocksLost(List<(string Path, bool IsPlaceholder)>? earlierFiles, ulong epoch, ulong startingIndex, ulong endingIndex, ulong from, ulong to, ulong era1Size)
    {
        foreach ((string earlierPath, bool isPlaceholder) in earlierFiles ?? [])
        {
            if (isPlaceholder) continue;

            ulong first, last;
            try
            {
                using E2StoreReader reader = new(earlierPath);
                (first, last) = (reader.First, reader.LastBlock);
            }
            catch (Exception e) when (e is EraException or ArgumentException)
            {
                throw new EraException($"Cannot read the block range of {Path.GetFileName(earlierPath)}: {e.Message} Remove or repair it before exporting this epoch.", e);
            }

            // The request holds the file, but this export's epochs start at another offset within an era.
            if ((first < startingIndex || last > endingIndex) && first >= from && last <= to)
                throw new EraException(
                    $"Cannot export blocks {startingIndex}-{endingIndex} as epoch {epoch}: era1 epochs start at the export's first block, " +
                    $"and {Path.GetFileName(earlierPath)} (blocks {first}-{last}) does not fit in that window. " +
                    $"Start the export at a block with the same offset within an era ({first % era1Size}), for example {first}, or export into another directory.");

            if (first < startingIndex || last > endingIndex)
                throw new EraException(
                    $"Cannot export blocks {startingIndex}-{endingIndex} as epoch {epoch}: replacing {Path.GetFileName(earlierPath)} (blocks {first}-{last}) would lose blocks. " +
                    "Export a range that contains them, or export into another directory.");
        }
    }

    /// <summary>
    /// Deletes the earlier files of an epoch except the one that now holds it. A name that differs from the survivor's
    /// only in case is a separate file only when the directory lists both names, as on a case-sensitive file system;
    /// elsewhere it is the survivor itself.
    /// </summary>
    private void DeleteSupersededFiles(IEnumerable<string> earlierFiles, string survivor)
    {
        string survivorName = Path.GetFileName(survivor);
        HashSet<string>? listed = null;
        foreach (string earlier in earlierFiles)
        {
            string name = Path.GetFileName(earlier);
            if (name == survivorName) continue;
            if (string.Equals(name, survivorName, StringComparison.OrdinalIgnoreCase))
            {
                listed ??= fileSystem.Directory.EnumerateFiles(Path.GetDirectoryName(survivor)!).Select(static f => Path.GetFileName(f)).ToHashSet(StringComparer.Ordinal);
                if (!listed.Contains(name) || !listed.Contains(survivorName)) continue;
            }

            fileSystem.File.Delete(earlier);
        }
    }

    private async Task WriteFileAsync(string path, SortedDictionary<long, (ValueHash256 Hash, string FileName)> entries, CancellationToken cancellationToken)
    {
        await using FileSystemStream stream = fileSystem.FileStream.New(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using StreamWriter writer = new(stream);

        foreach ((ValueHash256 hash, string fileName) in entries.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync(hash.ToString());
            await writer.WriteAsync(' ');
            await writer.WriteLineAsync(fileName);
        }
    }
}
