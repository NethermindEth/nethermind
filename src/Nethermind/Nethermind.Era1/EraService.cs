// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Era1;
public class EraService
{
    private const int MergeBlock = 15537393;
    private readonly IFileSystem _fileSystem;

    public EraService(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    //TODO cancellation
    public async Task Export(string destinationPath, string network, IBlockTree blockTree, IReceiptStorage receiptStorage, long start, long count, CancellationToken cancellation = default)
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

        const int remnant = MergeBlock % 8192;
        int currentEpoch = MergeBlock / 8192 + (remnant == 0 ? 0 : 1);

        //TODO read directly from RocksDb with range reads
        for (int i = MergeBlock - remnant; i >= 0; i = i - 8192)
        {
            string filePath = Path.Combine(
                                    destinationPath,
            EraBuilder.Filename(network, currentEpoch, Keccak.Zero));
            if (_fileSystem.File.Exists(filePath))
            {
                //TODO remove when existing files are handled
                continue;
            }
            using EraBuilder? builder = EraBuilder.Create(_fileSystem.File.Create(filePath));
            int y = i;
            while (true)
            {
                //TODO create level ??
                var b = blockTree.FindBlock(y);
                if (b == null)
                {
                    //TODO handle missing block
                    throw new EraException($"Missing block {y} during export.");
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
                    currentEpoch--;
                    break;
                }
                y++;
            }
        }
    }
    private int EnsureExistingEraFiles()
    {
        //TODO check existing ERA files in case this is a restart
        return 0;
    }

    public bool VerifyEraFiles(string path)
    {
        throw new NotImplementedException();
    }
}
