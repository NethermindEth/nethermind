// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Era1;
public class EraService : IEraService
{
    private const int MergeBlock = 15537393;
    private readonly IFileSystem _fileSystem;
    private readonly IBlockTree _blockTree;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    public EraService(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        ILogManager logManager)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree;
        _receiptStorage = receiptStorage;
        _specProvider = specProvider;
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public async Task TestImport(string src, string network)
    {
        var eraFiles = EraReader.GetAllEraFiles(src, network);

        foreach (var era in eraFiles)
        {
            using var eraEnumerator = await EraReader.Create(era);

            await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraEnumerator)
            {

            }
        }
    }
    public Task TestExport(
        string destinationPath,
        string network,

        long start,
        long count,
        CancellationToken cancellation = default)
    {
        return Export(destinationPath, network, start, count, cancellation);
    }

    //TODO cancellation
    public async Task Export(
        string destinationPath,
        string network,
        long start,
        long count,
        CancellationToken cancellation = default)
    {
        if (destinationPath is null) throw new ArgumentNullException(nameof(destinationPath));
        if (_fileSystem.File.Exists(destinationPath)) throw new ArgumentException(nameof(destinationPath), $"Cannot be a file.");

        EnsureExistingEraFiles();
        if (!_fileSystem.Directory.Exists(destinationPath))
        {
            //TODO look into permission settings - should it be set?
            _fileSystem.Directory.CreateDirectory(destinationPath);
        }
        var currentEpoch = 0;
        string filePath = Path.Combine(
                            destinationPath,
                    EraWriter.Filename(network, currentEpoch, Keccak.Zero));
        EraWriter? builder = EraWriter.Create(_fileSystem.File.Create(filePath), _specProvider);

        //TODO read directly from RocksDb with range reads
        for (var i = start; i < start + count; i++)
        {

            //TODO create level ??
            var b = _blockTree.FindBlock(i);
            if (b == null)
            {
                //TODO handle missing block
                throw new EraException($"Missing block {i} during export.");
            }
            //TODO test for pre-merge blocks?
            //if (b.Header.Difficulty == 0)
            //{
            //    //TODO handle this more gracefully
            //    await builder.Finalize();
            //    currentEpoch--;
            //    break;
            //}

            TxReceipt[]? receipts = _receiptStorage.Get(b);
            if (receipts == null)
            {
                //TODO  handle this scenario
                throw new EraException("receipts is null ??");
            }
            if (!await builder.Add(b, receipts))
            {
                await builder.Finalize();
                builder.Dispose();
                currentEpoch++;
                builder = EraWriter.Create(Path.Combine(
                            destinationPath,
                    EraWriter.Filename(network, currentEpoch, Keccak.Zero)), _specProvider);
            }
        }
    }
    private int EnsureExistingEraFiles()
    {
        //TODO check and handle existing ERA files in case this is a restart
        return 0;
    }

    public bool VerifyEraFiles(string path)
    {
        throw new NotImplementedException();
    }
}
