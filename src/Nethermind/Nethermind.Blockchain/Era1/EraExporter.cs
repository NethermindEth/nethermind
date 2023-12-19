// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.Core.Collections;
using System;
using System.Threading;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Era1;
using System.Linq;
using Org.BouncyCastle.Crypto.Generators;

namespace Nethermind.Blockchain;
public class EraExporter : IEraExporter
{
    private const int MergeBlock = 15537393;
    private readonly IFileSystem _fileSystem;
    private readonly IBlockTree _blockTree;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ISpecProvider _specProvider;
    private readonly string _networkName;
    private readonly ILogger _logger;

    public EraExporter(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        string networkName,
        ILogManager logManager)
    {
        if (logManager is null) throw new ArgumentNullException(nameof(logManager));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        if (string.IsNullOrWhiteSpace(networkName)) throw new ArgumentException("Cannot be null or whitespace.",nameof(specProvider));
        _networkName = networkName.Trim().ToLower();
        _logger = logManager.GetClassLogger();
    }

    public async Task Export(
        string destinationPath,
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

            //using StreamWriter checksumWriter = _fileSystem.File.CreateText(Path.Combine(destinationPath, "checksums.txt"));

            DateTime startTime = DateTime.Now;
            DateTime lastProgress = DateTime.Now;
            int processed = 0;
            int txProcessed = 0;

            for (long i = start; i <= end; i += size)
            {
                string filePath = Path.Combine(
                   destinationPath,
                   EraWriter.Filename(_networkName, i / size, Keccak.Zero));
                using EraWriter? builder = EraWriter.Create(_fileSystem.File.Create(filePath), _specProvider);

                //For compatibility reasons, we dont want to write a line terminator after the last checksum,
                //so we write one here instead, avoiding the last line
                //if (i != start)
                //    await checksumWriter.WriteLineAsync();

                //TODO read directly from RocksDb with range reads
                for (var y = i; y <= end; y++)
                {
                    Block? block = _blockTree.FindBlock(y, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);

                    if (block == null)
                    {
                        //Found the level but not the block?
                        throw new EraException($"Could not find a block with number {y}.");
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
                                                EraWriter.Filename(_networkName, i / size, new Hash256(root)));
                        _fileSystem.File.Move(filePath,
                            rename, true);
                        //await checksumWriter.WriteAsync(checksum.ToHexString(true));
                        break;
                    }
                    txProcessed += block.Transactions.Length;
                    processed++;
                    TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
                    if (elapsed.TotalSeconds > TimeSpan.FromSeconds(10).TotalSeconds)
                    {
                        if (_logger.IsInfo)
                            _logger.Info($"Export progress: {y,10}/{end} blocks  |  elapsed {DateTime.Now.Subtract(startTime):hh\\:mm\\:ss}  |  {processed / elapsed.TotalSeconds,10:0.##} Blk/s  |  {txProcessed / elapsed.TotalSeconds,10:0.##} tx/s");
                        lastProgress = DateTime.Now;
                        processed = 0;
                        txProcessed = 0;
                    }
                }
            }
            if (_logger.IsInfo) _logger.Info($"Finished history export from {start} to {end}");
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException) 
        {
            _logger.Error($"A running export job was cancelled. Exported archives in {destinationPath} might be in a corrupted state.");
        }
        catch (EraException e)
        {
            _logger.Error("Import error", e);
        }
        catch (Exception e)
        {
            _logger.Error("Export error", e);
            throw;
        }
    }

    private int EnsureExistingEraFiles()
    {
        //TODO check and handle existing ERA files in case this is a restart
        //What is the correct behavior? 
        return 0;
    }

    public async Task<bool> VerifyEraFiles(string[] eraFiles, byte[][] expectedAccumulators, CancellationToken cancellation = default)
    {
        if (expectedAccumulators is null) throw new ArgumentNullException(nameof(expectedAccumulators));
        if (eraFiles is null) throw new ArgumentNullException(nameof(eraFiles));
        if (eraFiles.Length != expectedAccumulators.Length)
            throw new ArgumentException("Must have an equal amount of files and accumulators.", nameof(eraFiles));
        int result = 1;
        using CancellationTokenSource cts = new CancellationTokenSource();
        cancellation.Register(cts.Cancel);
        await Parallel.ForEachAsync(Enumerable.Range(0, eraFiles.Length), new ParallelOptions()
        {
            CancellationToken = cts.Token,
            MaxDegreeOfParallelism = Environment.ProcessorCount / 2
        }, async (i, token ) =>
        {
            using EraReader reader = await EraReader.Create(eraFiles[i], token);
            if (!await reader.VerifyAccumulator(expectedAccumulators[i], _specProvider, token))
            {
                Interlocked.Exchange(ref result, 0);
                cts.Cancel();
            }
        });

        return Convert.ToBoolean(result);
    }
}
