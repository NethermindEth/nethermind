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
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.Core.Collections;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Nethermind.Era1;
using System.Text;
using Nethermind.Evm;
using System.ComponentModel;
using MathNet.Numerics.Distributions;

namespace Nethermind.Synchronization;
public class EraImport : IEraImport
{
    private const int MergeBlock = 15537393;
    private readonly IFileSystem _fileSystem;
    private readonly IBlockTree _blockTree;
    private readonly IBlockValidator _blockValidator;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ISpecProvider _specProvider;
    private readonly string _networkName;

    public event EventHandler<ImportProgressChangedArgs> ImportProgressChanged;

    public EraImport(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        string networkName)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree;
        _blockValidator = blockValidator;
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        if (string.IsNullOrWhiteSpace(networkName)) throw new ArgumentException("Cannot be null or whitespace.", nameof(specProvider));
        _networkName = networkName.Trim().ToLower();
    }

    public Task ImportAsFullSync(string src, CancellationToken cancellation)
    {
        return ImportInternal(src, _blockTree.Head.Number, true, true, true, cancellation);
    }

    public Task Import(
        string src,
        long startNumber,
        bool insertBodies,
        bool insertReceipts,
        CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(src)) throw new ArgumentException("Cannot be null or empty.", nameof(src));
        if (startNumber < 0) throw new ArgumentOutOfRangeException("Cannot be negative.", startNumber, nameof(startNumber));

        return ImportInternal(src, startNumber, insertBodies, insertReceipts, false, cancellation);
    }

    private async Task ImportInternal(
        string src,
        long startNumber,
        bool insertBodies,
        bool insertReceipts,
        bool processBlock,
        CancellationToken cancellation)
    {
        var eraFiles = EraReader.GetAllEraFiles(src, _networkName).ToArray();

        EraStore eraStore = new(eraFiles, _fileSystem);

        long startEpoch = startNumber / EraWriter.MaxEra1Size;

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

        int increment = 1;
        for (long i = startEpoch; eraStore.HasEpoch(i); i = i + increment)
        {
            using EraReader eraReader = await eraStore.GetReader(i, cancellation);

            await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraReader)
            {
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

                await SuggestBlock(b, r, processBlock, cancellation);

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

    private void InsertValidatedBlock(Block block, TxReceipt[] receipts, bool insertBodies, bool insertReceipts, CancellationToken cancellation)
    {
        AddBlockResult result = _blockTree.Insert(block.Header);

        EnsureAddResult(block, result);

        if (insertBodies)
        {
            result = _blockTree.Insert(block, BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks, bodiesWriteFlags: WriteFlags.DisableWAL);
            EnsureAddResult(block, result);
        }

        if (insertReceipts)
        {
            _receiptStorage.Insert(block, receipts, ensureCanonical: true);
        }
    }

    private static void EnsureAddResult(Block block, AddBlockResult result)
    {
        if (result != AddBlockResult.Added && result != AddBlockResult.AlreadyKnown)
            throw new EraImportException($"Attempted to insert {block.ToString(Block.Format.Short)}, but received an unexpected result ({result}).");
    }

    private async Task SuggestBlock(Block block, TxReceipt[] receipts, bool processBlock,CancellationToken cancellation)
    {
        var options = processBlock ? BlockTreeSuggestOptions.ShouldProcess: BlockTreeSuggestOptions.None;
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
                _receiptStorage.Insert(block, receipts);
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

    public Task<bool> VerifyEraFiles(string path)
    {
        var eraFiles = EraReader.GetAllEraFiles(path, "mainnet");

        throw new NotImplementedException();
    }

}
