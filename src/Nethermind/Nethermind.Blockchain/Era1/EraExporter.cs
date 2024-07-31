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
using System.Collections.Generic;

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

    public string NetworkName => _networkName;

    public const string AccumulatorFileName = "accumulators.tx";

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
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(size), size, $"Must be greater than 0.");
        if (size > EraWriter.MaxEra1Size) throw new ArgumentOutOfRangeException(nameof(size), size, $"Cannot be greater than {EraWriter.MaxEra1Size}.");
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

        List<byte[]> eraRoots = new ();
        string accumulatorPath = Path.Combine(destinationPath, AccumulatorFileName);
        _fileSystem.File.Delete(accumulatorPath);
        for (long i = start; i <= end; i += size)
        {
            string filePath = Path.Combine(
               destinationPath,
               EraWriter.Filename(_networkName, i / size, Keccak.Zero));
            using EraWriter? builder = EraWriter.Create(_fileSystem.File.Create(filePath), _specProvider);

            //TODO read directly from RocksDb with range reads
            for (var y = i; y <= i + size; y++)
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
                if (!await builder.Add(block, receipts, cancellation) || y == i + size || y == end)
                {
                    byte[] root = await builder.Finalize();
                    builder.Dispose();
                    string rename = Path.Combine(
                                            destinationPath,
                                            EraWriter.Filename(_networkName, i / size, new Hash256(root)));
                    _fileSystem.File.Move(
                        filePath,
                        rename, true);
                    eraRoots.Add(root);
                    break;
                }
                totalProcessed++;
                txProcessedSinceLast += block.Transactions.Length;
                processedSinceLast++;
                TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
                if (elapsed.TotalSeconds > TimeSpan.FromSeconds(10).TotalSeconds)
                {
                    ExportProgress?.Invoke(this, new ExportProgressArgs(
                        end - start,
                        totalProcessed,
                        processedSinceLast,
                        txProcessedSinceLast,
                        elapsed,
                        DateTime.Now.Subtract(startTime)));
                    lastProgress = DateTime.Now;
                    processedSinceLast = 0;
                    txProcessedSinceLast = 0;
                }
            }
        }
        _fileSystem.File.WriteAllLines(accumulatorPath, eraRoots.Select(s=>s.ToHexString(true)));
    }
}
