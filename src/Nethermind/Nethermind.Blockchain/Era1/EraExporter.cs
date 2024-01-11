// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using System;
using System.Threading;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Era1;
using System.Linq;
using Nethermind.Blockchain.Era1;

namespace Nethermind.Blockchain;
public class EraExporter : IEraExporter
{
    private const int MergeBlock = 15537393;
    private readonly IFileSystem _fileSystem;
    private readonly IBlockTree _blockTree;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ISpecProvider _specProvider;
    private readonly string _networkName;

    public event EventHandler<ExportProgressArgs> ExportProgress;
    public event EventHandler<VerificationProgressArgs> VerificationProgress;

    public EraExporter(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        string networkName)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        if (string.IsNullOrWhiteSpace(networkName)) throw new ArgumentException("Cannot be null or whitespace.", nameof(specProvider));
        _networkName = networkName.Trim().ToLower();
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

        if (!_fileSystem.Directory.Exists(destinationPath))
        {
            //TODO look into permission settings - should it be set?
            _fileSystem.Directory.CreateDirectory(destinationPath);
        }

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

            //TODO read directly from RocksDb with range reads
            for (var y = i; y <= end; y++)
            {
                Block? block = _blockTree.FindBlock(y, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);

                if (block == null)
                {
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
                    builder.Dispose();
                    string rename = Path.Combine(
                                            destinationPath,
                                            EraWriter.Filename(_networkName, i / size, new Hash256(root)));
                    _fileSystem.File.Move(
                        filePath,
                        rename, true);
                    break;
                }
                txProcessed += block.Transactions.Length;
                processed++;
                TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
                if (elapsed.TotalSeconds > TimeSpan.FromSeconds(10).TotalSeconds)
                {
                    ExportProgress?.Invoke(this, new ExportProgressArgs(end, processed, txProcessed, elapsed, DateTime.Now.Subtract(startTime)));
                    lastProgress = DateTime.Now;
                    processed = 0;
                    txProcessed = 0;
                }
            }
        }
    }
    /// <summary>
    /// Verifies all era1 archives from a directory, with an expected accumulator list from a hex encoded file.
    /// </summary>
    /// <param name="eraDirectory"></param>
    /// <param name="accumulatorFile"></param>
    /// <param name="cancellation"></param>
    /// <exception cref="EraVerificationException">If the verification fails.</exception>
    public async Task VerifyEraFiles(string eraDirectory, string accumulatorFile, CancellationToken cancellation = default)
    {
        string[] eraFiles = EraReader.GetAllEraFiles(eraDirectory, _networkName).ToArray();
        string[] lines = await _fileSystem.File.ReadAllLinesAsync(accumulatorFile, cancellation);

        byte[][] accumulators = lines.Select(s => Bytes.FromHexString(s)).ToArray();

        await VerifyEraFiles(eraFiles, accumulators);
    }
    /// <summary>
    /// Verifies all era1 files, with an expected accumulator list.
    /// </summary>
    /// <param name="eraFiles"></param>
    /// <param name="expectedAccumulators"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="EraVerificationException">If the verification fails.</exception>
    public async Task VerifyEraFiles(string[] eraFiles, byte[][] expectedAccumulators, CancellationToken cancellation = default)
    {
        if (expectedAccumulators is null) throw new ArgumentNullException(nameof(expectedAccumulators));
        if (eraFiles is null) throw new ArgumentNullException(nameof(eraFiles));
        if (eraFiles.Length != expectedAccumulators.Length)
            throw new ArgumentException("Must have an equal amount of files and accumulators.", nameof(eraFiles));

        DateTime lastProgress = DateTime.Now; 
        for (int i = 0; i < eraFiles.Length; i++)
        {
            using EraReader reader = await EraReader.Create(eraFiles[i], cancellation);
            if (!await reader.VerifyAccumulator(expectedAccumulators[i], _specProvider, cancellation))
            {
                throw new EraVerificationException($"The accumulator for the archive '{eraFiles[i]}' does not match the expected accumulator '{expectedAccumulators[i]}'");
            }

            TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
            if (elapsed.TotalSeconds > TimeSpan.FromSeconds(10).TotalSeconds)
            {
                VerificationProgress?.Invoke(this, new VerificationProgressArgs(i, eraFiles.Length, elapsed));
                lastProgress = DateTime.Now;
            }
        }
    }
}
