// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State.Proofs;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Nethermind.Era1;

namespace Nethermind.Synchronization;
public class EraImporter : IEraImporter
{
    private const int MergeBlock = 15537393;
    private readonly IFileSystem _fileSystem;
    private readonly IBlockTree _blockTree;
    private readonly IBlockValidator _blockValidator;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ISpecProvider _specProvider;
    private readonly int _epochSize;
    private readonly string _networkName;

    public event EventHandler<ImportProgressChangedArgs> ImportProgressChanged;

    public EraImporter(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        string networkName,
        int epochSize = EraWriter.MaxEra1Size)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree;
        _blockValidator = blockValidator;
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        this._epochSize = epochSize;
        if (string.IsNullOrWhiteSpace(networkName)) throw new ArgumentException("Cannot be null or whitespace.", nameof(specProvider));
        _networkName = networkName.Trim().ToLower();
    }

    public Task ImportAsArchiveSync(string src, CancellationToken cancellation)
    {
        return ImportInternal(src, _blockTree.Head?.Number + 1 ?? 0, true, false, true, cancellation);
    }

    private async Task ImportInternal(
        string src,
        long startNumber,
        bool insertBodies,
        bool insertReceipts,
        bool processBlock,
        CancellationToken cancellation)
    {
        var eraFiles = EraReader.GetAllEraFiles(src, _networkName, _fileSystem).ToArray();

        EraStore eraStore = new(eraFiles, _fileSystem);

        long startEpoch = startNumber / _epochSize;

        if (!eraStore.HasEpoch(startEpoch))
        {
            throw new EraImportException($"No {_networkName} epochs found for block {startNumber} in '{src}'");
        }

        DateTime lastProgress = DateTime.Now;
        long epochProcessed = 0;
        DateTime startTime = DateTime.Now;
        long txProcessed = 0;
        long totalblocks = 0;
        int blocksProcessed = 0;

        for (long i = startEpoch; eraStore.HasEpoch(i); i++)
        {
            using EraReader eraReader = await eraStore.GetReader(i, cancellation);

            await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraReader)
            {
                cancellation.ThrowIfCancellationRequested();

                if (b.IsGenesis)
                {
                    continue;
                }

                if (b.Number < startNumber)
                {
                    continue;
                }

                if (insertBodies)
                {
                    if (b.IsBodyMissing)
                    {
                        throw new EraImportException($"Unexpected block without a body found in '{eraStore.GetReaderPath(i)}'. Archive might be corrupted.");
                    }

                    if (!_blockValidator.ValidateSuggestedBlock(b))
                    {
                        throw new EraImportException($"Era1 archive '{eraStore.GetReaderPath(i)}' contains an invalid block {b.ToString(Block.Format.Short)}.");
                    }
                }

                if (insertReceipts)
                {
                    ValidateReceipts(b, r);
                }
                cancellation.ThrowIfCancellationRequested();
                await SuggestBlock(b, r, processBlock);

                blocksProcessed++;
                txProcessed += b.Transactions.Length;
                TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
                if (elapsed.TotalSeconds > TimeSpan.FromSeconds(10).TotalSeconds)
                {
                    ImportProgressChanged?.Invoke(this, new ImportProgressChangedArgs(DateTime.Now.Subtract(startTime), blocksProcessed, txProcessed, totalblocks, epochProcessed, eraStore.EpochCount));
                    lastProgress = DateTime.Now;
                }
            }
            epochProcessed++;
        }
        ImportProgressChanged?.Invoke(this, new ImportProgressChangedArgs(DateTime.Now.Subtract(startTime), blocksProcessed, txProcessed, totalblocks, epochProcessed, eraStore.EpochCount));
    }

    private async Task SuggestBlock(Block block, TxReceipt[] receipts, bool processBlock)
    {
        var options = processBlock ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None;
        var addResult = await _blockTree.SuggestBlockAsync(block, options);
        switch (addResult)
        {
            case AddBlockResult.AlreadyKnown:
                return;
            case AddBlockResult.CannotAccept:
                throw new EraImportException("Rejected block in Era1 archive");
            case AddBlockResult.UnknownParent:
                throw new EraImportException("Unknown parent for block in Era1 archive");
            case AddBlockResult.InvalidBlock:
                throw new EraImportException("Invalid block in Era1 archive");
            case AddBlockResult.Added:
                if (!processBlock) _receiptStorage.Insert(block, receipts);
                break;
            default:
                throw new NotSupportedException($"Not supported value of {nameof(AddBlockResult)} = {addResult}");
        }
    }

    private void ValidateReceipts(Block block, TxReceipt[] blockReceipts)
    {
        Hash256 receiptsRoot = new ReceiptTrie(_specProvider.GetSpec(block.Header), blockReceipts).RootHash;

        if (receiptsRoot != block.ReceiptsRoot)
        {
            throw new EraImportException($"Wrong receipts root in Era1 archive for block {block.ToString(Block.Format.Short)}.");
        }
    }
}
