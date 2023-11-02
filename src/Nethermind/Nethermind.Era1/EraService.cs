// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Era1;
public class EraService
{
    private const int MergeBlock = 15537393;
    private readonly IFileSystem _fileSystem;

    public EraService(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
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
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        long start,
        long count,
        CancellationToken cancellation = default)
    {
        return Export(destinationPath, network, blockTree, receiptStorage, start, count, cancellation);
    }

    //TODO cancellation
    public async Task Export(
        string destinationPath,
        string network,
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        long start,
        long count,
        CancellationToken cancellation = default)
    {
        if (destinationPath is null) throw new ArgumentNullException(nameof(destinationPath));
        if (blockTree is null) throw new ArgumentNullException(nameof(blockTree));
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
                    EraBuilder.Filename(network, currentEpoch, Keccak.Zero));
        EraBuilder? builder = EraBuilder.Create(_fileSystem.File.Create(filePath));

        //TODO read directly from RocksDb with range reads
        for (var i = start; i < start + count; i++)
        {
            
            //TODO create level ??
            var b = blockTree.FindBlock(i);
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

            TxReceipt[]? receipts = receiptStorage.Get(b);
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
                builder = EraBuilder.Create(Path.Combine(
                            destinationPath,
                    EraBuilder.Filename(network, currentEpoch, Keccak.Zero)));
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
