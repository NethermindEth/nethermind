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
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Nethermind.Era1;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Core.Extensions;
using System.IO;

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
    private readonly ReceiptMessageDecoder _receiptDecoder;

    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(10);

    public event EventHandler<ImportProgressChangedArgs> ImportProgressChanged;
    public event EventHandler<VerificationProgressArgs> VerificationProgress;

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
        _receiptDecoder = new();
        this._epochSize = epochSize;
        if (string.IsNullOrWhiteSpace(networkName)) throw new ArgumentException("Cannot be null or whitespace.", nameof(specProvider));
        _networkName = networkName.Trim().ToLower();
    }

    public async Task Import(string src, long start, long end, string? accumulatorFile, CancellationToken cancellation = default)
    {
        string[] eraFiles = EraReader.GetAllEraFiles(src, _networkName, _fileSystem).ToArray();

        EraStore eraStore = new(eraFiles, _fileSystem);

        long headNumber = _blockTree.Head.Number;

        if (!string.IsNullOrEmpty(accumulatorFile))
        {
            await VerifyEraFiles(src, accumulatorFile, cancellation);
        }
        await ImportInternal(src, start, true, true, false, cancellation);
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
                    string msg;
                    if (!_blockValidator.ValidateSuggestedBlock(b, out msg))
                    {
                        throw new EraImportException($"Era1 archive '{eraStore.GetReaderPath(i)}' contains an invalid block {b.ToString(Block.Format.Short)}: {msg}");
                    }
                }

                cancellation.ThrowIfCancellationRequested();
                if (processBlock)
                    await SuggestBlock(b, r, processBlock);
                else
                    InsertBlock(b,r);

                if (insertReceipts)
                {
                    ValidateReceipts(b, r);
                }
                blocksProcessed++;
                txProcessed += b.Transactions.Length;
                TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
                if (elapsed.TotalSeconds > ProgressInterval.TotalSeconds)
                {
                    ImportProgressChanged?.Invoke(this, new ImportProgressChangedArgs(DateTime.Now.Subtract(startTime), blocksProcessed, txProcessed, totalblocks, epochProcessed, eraStore.EpochCount));
                    lastProgress = DateTime.Now;
                }
            }
            epochProcessed++;
        }
        ImportProgressChanged?.Invoke(this, new ImportProgressChangedArgs(DateTime.Now.Subtract(startTime), blocksProcessed, txProcessed, totalblocks, epochProcessed, eraStore.EpochCount));
    }

    private void InsertBlock(Block b, TxReceipt[] r)
    {
        _blockTree.Insert(b, BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks, bodiesWriteFlags: WriteFlags.DisableWAL);
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


    /// <summary>
    /// Verifies all era1 archives from a directory, with an expected accumulator list from a hex encoded file.
    /// </summary>
    /// <param name="eraDirectory"></param>
    /// <param name="accumulatorFile"></param>
    /// <param name="cancellation"></param>
    /// <exception cref="EraVerificationException">If the verification fails.</exception>
    public async Task VerifyEraFiles(string eraDirectory, string accumulatorFile, CancellationToken cancellation = default)
    {
        if (!_fileSystem.Directory.Exists(eraDirectory))
            throw new EraImportException($"Directory does not exist '{eraDirectory}'");
        if (!_fileSystem.File.Exists(accumulatorFile))
            throw new EraImportException($"Accumulator file does not exist '{accumulatorFile}'");
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
        if (eraFiles.Length == 0)
            throw new ArgumentException("Must have at least one file.", nameof(eraFiles));
        if (eraFiles.Length != expectedAccumulators.Length)
            throw new ArgumentException("Must have an equal amount of files and accumulators.", nameof(eraFiles));
        if (expectedAccumulators.Any(a => a.Length != 32))
            throw new ArgumentException("All accumulators must have a length of 32 bytes.", nameof(eraFiles));

        DateTime startTime = DateTime.Now;
        DateTime lastProgress = DateTime.Now;
        for (int i = 0; i < eraFiles.Length; i++)
        {
            using EraReader reader = await EraReader.Create(_fileSystem.File.OpenRead(eraFiles[i]), cancellation);
            if (!await reader.VerifyAccumulator(expectedAccumulators[i], _specProvider, cancellation))
            {
                throw new EraVerificationException($"The accumulator for the archive '{eraFiles[i]}' does not match the expected accumulator '{expectedAccumulators[i].ToHexString()}'.");
            }

            TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
            if (elapsed.TotalSeconds > ProgressInterval.TotalSeconds)
            {
                VerificationProgress?.Invoke(this, new VerificationProgressArgs(i, eraFiles.Length, DateTime.Now.Subtract(startTime)));
                lastProgress = DateTime.Now;
            }
        }
    }

    private void ValidateReceipts(Block block, TxReceipt[] blockReceipts)
    {
        Hash256 receiptsRoot = new ReceiptTrie<TxReceipt>(_specProvider.GetSpec(block.Header), blockReceipts, _receiptDecoder).RootHash;

        if (receiptsRoot != block.ReceiptsRoot)
        {
            throw new EraImportException($"Wrong receipts root in Era1 archive for block {block.ToString(Block.Format.Short)}.");
        }
    }
}
