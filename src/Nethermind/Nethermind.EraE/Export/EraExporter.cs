// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.EraE.Archive;
using Nethermind.EraE.Config;
using EraException = Nethermind.Era1.EraException;
using Nethermind.EraE.Proofs;
using Nethermind.Logging;

namespace Nethermind.EraE.Export;

public class EraExporter(
    IFileSystem fileSystem,
    IBlockTree blockTree,
    IReceiptStorage receiptStorage,
    ISpecProvider specProvider,
    IEraEConfig eraConfig,
    ILogManager logManager,
    IBeaconRootsProvider? beaconRootsProvider = null)
    : IEraExporter
{
    private readonly string _networkName = string.IsNullOrWhiteSpace(eraConfig.NetworkName)
        ? throw new ArgumentException("NetworkName cannot be null or whitespace.", nameof(eraConfig))
        : eraConfig.NetworkName.Trim().ToLower();

    private readonly ILogger _logger = logManager.GetClassLogger<EraExporter>();
    private readonly int _eraSize = eraConfig.MaxEraSize is > 0 and <= EraWriter.MaxEraSize
        ? eraConfig.MaxEraSize
        : throw new ArgumentException($"MaxEraSize must be between 1 and {EraWriter.MaxEraSize} (SLOTS_PER_HISTORICAL_ROOT). Got {eraConfig.MaxEraSize}.");

    public const string AccumulatorFileName = "accumulators.txt";
    public const string ChecksumsFileName = "checksums_sha256.txt";

    private const int ProgressLogInterval = 10000;
    private const int RetryDelayMs = 100;

    public Task Export(string destinationPath, long from, long to, CancellationToken cancellation = default)
    {
        if (fileSystem.File.Exists(destinationPath))
            throw new ArgumentException("Destination already exists as a file.", nameof(destinationPath));
        if (to == 0) to = blockTree.Head?.Number ?? 0;
        if (to > (blockTree.Head?.Number ?? 0))
            throw new ArgumentException($"Cannot export beyond head block {blockTree.Head?.Number ?? 0}.");
        if (from > to)
            throw new ArgumentException($"Start block ({from}) must not be after end block ({to}).");

        Block? lastBlock = blockTree.FindBlock(to, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (lastBlock is null)
            throw new InvalidOperationException(
                $"Block {to} is not available. " +
                "EraE export requires all block bodies to be present. " +
                "Ensure the node is fully synced before exporting.");

        return DoExport(destinationPath, from, to, cancellation);
    }

    private async Task DoExport(string destinationPath, long from, long to, CancellationToken cancellation)
    {
        int concurrency = CalculateConcurrency(eraConfig.Concurrency);
        if (_logger.IsInfo) _logger.Info($"Exporting EraE blocks {from}–{to} to {destinationPath} (concurrency={concurrency})");

        if (!fileSystem.Directory.Exists(destinationPath))
            fileSystem.Directory.CreateDirectory(destinationPath);

        ProgressLogger progress = new("EraE export", logManager);
        progress.Reset(0, to - from + 1);
        int totalProcessed = 0;

        long startEpoch = from / _eraSize;
        long endEpoch = to / _eraSize;
        long epochCount = endEpoch - startEpoch + 1;

        using ArrayPoolList<long> epochIdxs = new((int)epochCount);
        for (long i = 0; i < epochCount; i++) epochIdxs.Add(i);

        using ArrayPoolList<ValueHash256> accumulators = new((int)epochCount, (int)epochCount);
        using ArrayPoolList<ValueHash256> checksums = new((int)epochCount, (int)epochCount);
        using ArrayPoolList<string> fileNames = new((int)epochCount, (int)epochCount);

        // Load existing hash manifests so TrySkipExistingEpoch can look up checksums and
        // accumulator roots by filename — O(1) per epoch — instead of reading each era file.
        Dictionary<string, ValueHash256> cachedChecksums = LoadHashFile(Path.Combine(destinationPath, ChecksumsFileName));
        Dictionary<string, ValueHash256> cachedAccumulators = LoadHashFile(Path.Combine(destinationPath, AccumulatorFileName));

        await Parallel.ForEachAsync(epochIdxs, new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
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
            int idx = (int)epochIdx;
            long epoch = startEpoch + epochIdx;
            // Each epoch covers [epoch * eraSize, epoch * eraSize + eraSize - 1].
            // Clamp to [from, to] to handle partial first and last epochs.
            long epochBlockStart = epoch * _eraSize;
            long writeFrom = Math.Max(epochBlockStart, from);
            long writeTo = Math.Min(epochBlockStart + _eraSize - 1, to);

            if (TrySkipExistingEpoch(destinationPath, epoch, idx, writeFrom, writeTo, accumulators, checksums, fileNames, cachedChecksums, cachedAccumulators, ref totalProcessed))
                return;

            string placeholderPath = Path.Combine(destinationPath, EraPathUtils.Filename(_networkName, epoch, Keccak.Zero));

            ValueHash256 accumulator;
            ValueHash256 sha256;
            Hash256 lastBlockHash = Keccak.Zero;

            using (EraWriter eraWriter = new(fileSystem.File.Create(placeholderPath), specProvider, beaconRootsProvider))
            {
                for (long blockNumber = writeFrom; blockNumber <= writeTo; blockNumber++)
                {
                    Block? block = blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
                    if (block is null)
                        throw new EraException($"Could not find block {blockNumber}. The node may not have finished syncing block bodies for this range.");

                    // IsPostMerge is not part of the RLP encoding and defaults to false when read from
                    // the block store. Restore it from Difficulty (EIP-3675: post-merge Difficulty == 0).
                    block.Header.IsPostMerge = block.Header.Difficulty == 0;

                    TxReceipt[]? receipts = receiptStorage.Get(block, true, false);
                    if (receipts is null || (block.Header.ReceiptsRoot != Keccak.EmptyTreeHash && receipts.Length == 0))
                        throw new EraException($"Could not find receipts for block {block.ToString(Block.Format.FullHashAndNumber)}.");

                    await eraWriter.Add(block, receipts, cancel);
                    lastBlockHash = block.Hash!;

                    if ((Interlocked.Increment(ref totalProcessed) % ProgressLogInterval) == 0)
                    {
                        progress.Update(totalProcessed);
                        progress.LogProgress();
                    }
                }

                (accumulator, sha256) = await eraWriter.Finalize(cancel);
            }

            // Safe concurrent indexed writes: each epoch has a unique idx slot;
            // reads from these arrays happen only after Parallel.ForEachAsync completes.
            accumulators[idx] = accumulator;
            checksums[idx] = sha256;
            // Filename uses the last block hash as the epoch identifier — same convention as go-ethereum execdb.
            string finalName = EraPathUtils.Filename(_networkName, epoch, lastBlockHash);
            fileNames[idx] = finalName;

            string finalPath = Path.Combine(destinationPath, finalName);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        fileSystem.File.Move(placeholderPath, finalPath, true);
                        break;
                    }
                    catch (IOException) when (attempt < 3)
                    {
                        await Task.Delay(RetryDelayMs * (attempt + 1), cancel);
                    }
                }
            }
            catch
            {
                fileSystem.File.Delete(placeholderPath);
                throw;
            }
        }
    }

    // Follows the same convention as FullPruningMaxDegreeOfParallelism / VisitingOptions.AdjustMaxDegreeOfParallelism:
    //   0  → ProcessorCount / 4 (default: background-friendly, leaves 75% of CPU for the live node)
    //  -1  → ProcessorCount     (aggressive: fastest export, use when the node has no other responsibilities)
    //  >0  → exact value
    private static int CalculateConcurrency(int configured) => configured switch
    {
        0 => Math.Max(Environment.ProcessorCount / 4, 1),
        < 0 => Environment.ProcessorCount,
        _ => configured
    };

    private bool TrySkipExistingEpoch(
        string destinationPath,
        long epoch,
        int idx,
        long writeFrom,
        long writeTo,
        ArrayPoolList<ValueHash256> accumulators,
        ArrayPoolList<ValueHash256> checksums,
        ArrayPoolList<string> fileNames,
        Dictionary<string, ValueHash256> cachedChecksums,
        Dictionary<string, ValueHash256> cachedAccumulators,
        ref int totalProcessed)
    {
        string? existingFile = fileSystem.Directory
            .EnumerateFiles(destinationPath, $"{_networkName}-{epoch:D5}-*{EraPathUtils.FileExtension}")
            .FirstOrDefault();

        if (existingFile is null)
            return false;

        string fileName = Path.GetFileName(existingFile);
        fileNames[idx] = fileName;

        if (cachedChecksums.TryGetValue(fileName, out ValueHash256 cachedChecksum))
        {
            // Fast path: both values available from the manifest files — no era file reads needed.
            checksums[idx] = cachedChecksum;
            accumulators[idx] = cachedAccumulators.GetValueOrDefault(fileName);
        }
        else
        {
            // Slow path: manifest missing or stale — read the era file directly.
            using EraReader reader = new(existingFile);
            try
            {
                accumulators[idx] = reader.ReadAccumulatorRoot();
            }
            catch (EraException)
            {
                accumulators[idx] = default;
            }
            checksums[idx] = reader.CalculateChecksum();
        }

        Interlocked.Add(ref totalProcessed, (int)(writeTo - writeFrom + 1));
        if (_logger.IsDebug) _logger.Debug($"Skipping already exported epoch {epoch}.");
        return true;
    }

    /// <summary>
    /// Parses a hash manifest file (checksums or accumulators) into a filename → hash lookup.
    /// Returns an empty dictionary if the file does not exist or cannot be read.
    /// </summary>
    private Dictionary<string, ValueHash256> LoadHashFile(string path)
    {
        Dictionary<string, ValueHash256> result = new();
        if (!fileSystem.File.Exists(path))
            return result;

        foreach (string line in fileSystem.File.ReadAllLines(path))
        {
            // Format: "{hash}  {filename}"  (two spaces, same as sha256sum output)
            int sep = line.IndexOf("  ", StringComparison.Ordinal);
            if (sep < 0) continue;

            string hashHex = line[..sep].Trim();
            string fileName = line[(sep + 2)..].Trim();
            if (hashHex.Length != 64) continue;
            result[fileName] = new ValueHash256(hashHex);
        }

        return result;
    }

    private async Task WriteHashFile(string path, ArrayPoolList<ValueHash256> hashes, ArrayPoolList<string> fileNames, CancellationToken cancellation)
    {
        await using FileSystemStream stream = fileSystem.FileStream.New(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using StreamWriter writer = new(stream);

        for (int i = 0; i < hashes.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            await writer.WriteLineAsync($"{hashes[i].ToString(false)}  {fileNames[i]}");
        }
    }
}
