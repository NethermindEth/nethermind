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
    private readonly ILogger _logger;

    public EraImport(
        IFileSystem fileSystem,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        string networkName,
        ILogManager logManager)
    {
        if (logManager is null) throw new ArgumentNullException(nameof(logManager));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _blockTree = blockTree;
        _blockValidator = blockValidator;
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        if (string.IsNullOrWhiteSpace(networkName)) throw new ArgumentException("Cannot be null or whitespace.", nameof(specProvider));
        _networkName = networkName.Trim().ToLower();
        _logger = logManager.GetClassLogger();
    }

    public async Task Import(string src, CancellationToken cancellation)
    {
        try
        {
            if (_logger.IsInfo) _logger.Info($"Starting importing blocks");

            var eraFiles = EraReader.GetAllEraFiles(src, _networkName).ToArray();

            EraStore eraStore = new(eraFiles, _fileSystem);

            long currentHeadEpoch = _blockTree.Head.Number / EraWriter.MaxEra1Size;
            if (eraStore.BiggestEpoch < currentHeadEpoch)
            {
                _logger.Info($"Skipping import since current head is ahead of era1 archives in '{src}'");
                return;
            }
            if (eraStore.SmallestEpoch > currentHeadEpoch)
            {
                _logger.Info($"Skipping import since era1 archives in '{src}' is ahead of current head.");
                return;
            }

            DateTime lastProgress = DateTime.Now;
            long epochProcessed = currentHeadEpoch;
            DateTime startTime = DateTime.Now;
            int totalCount = 0;

            const int batchSize = 1024;

            using ArrayPoolList<Block> blocks = new ArrayPoolList<Block>(batchSize);
            using ArrayPoolList<TxReceipt[]> receipts = new ArrayPoolList<TxReceipt[]>(batchSize);
            for (long i = currentHeadEpoch; i < eraStore.EpochCount; i++)
            {
                using EraReader eraReader = await eraStore.GetReader(i, cancellation);

                int count = 0;
                await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraReader)
                {
                    blocks.Add(b);
                    receipts.Add(r);

                    count++;
                    totalCount++;
                    if (count == batchSize)
                    {
                        await ImportBlocks(blocks, receipts, cancellation);
                        blocks.Clear();
                        receipts.Clear();
                        count = 0;
                    }

                    TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
                    if (elapsed.TotalSeconds > TimeSpan.FromSeconds(10).TotalSeconds)
                    {
                        if (_logger.IsInfo)
                        {
                            _logger.Info($"Import {epochProcessed,10}/{eraStore.EpochCount} epochs  |  elapsed {DateTime.Now.Subtract(startTime),7:hh\\:mm\\:ss} | {totalCount / DateTime.Now.Subtract(startTime).TotalSeconds,7:F2} Blks/s");
                        }

                        lastProgress = DateTime.Now;
                    }
                }
                if (count > 0)
                {
                    await ImportBlocks(blocks, receipts, cancellation);
                }
                epochProcessed++;
            }
            if (_logger.IsInfo) _logger.Info($"Finished importing {totalCount} blocks");

        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Warn($"A running import job was cancelled.");
        }
        catch (EraException e)
        {
            _logger.Error("Import error", e);
        }
        catch (Exception e)
        {
            _logger.Error("Import error", e);
            throw;
        }
    }

    private async Task ImportBlocks(ArrayPoolList<Block> blocks, ArrayPoolList<TxReceipt[]> receipts, CancellationToken cancellation)
    {
        if (!blocks.Any())
            return;

        if (blocks.Count != receipts.Count)
            throw new ArgumentException("There must be an equal amount of blocks and receipts.", nameof(blocks));

        for (int i = 0; i < blocks.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();

            Block block = blocks[i];
            TxReceipt[] receipt = receipts[i];

            if (block.IsGenesis)
                continue;

            if (block.IsBodyMissing)
            {
                throw new InvalidOperationException($"A block without a body was passed.");
            }

            if (!_blockValidator.ValidateSuggestedBlock(block))
            {
                throw new EraException($"Era1 archive contains an invalid block {block.ToString(Block.Format.Short)}.");
            }

            ValidateReceipts(block, receipt);

            var addResult = await _blockTree.SuggestBlockAsync(block, BlockTreeSuggestOptions.ShouldProcess);
            switch (addResult)
            {
                case AddBlockResult.AlreadyKnown:
                    continue;
                case AddBlockResult.CannotAccept:
                    throw new EraException("Rejected block in Era1 archive");
                case AddBlockResult.UnknownParent:
                    throw new EraException("Unknown parent for block in Era1 archive");
                case AddBlockResult.InvalidBlock:
                    throw new EraException("Invalid block in Era1 archive");
                case AddBlockResult.Added:
                    _receiptStorage.Insert(block, receipt);
                    break;
                default:
                    throw new NotSupportedException($"Not supported value of {nameof(AddBlockResult)} = {addResult}");
            }
        }
    }
    private void ValidateReceipts(Block block, TxReceipt[] blockReceipts)
    {
        Hash256 receiptsRoot = new ReceiptTrie(_specProvider.GetSpec(block.Header), blockReceipts).RootHash;

        if (receiptsRoot != block.ReceiptsRoot)
        {
            throw new EraException($"Wrong receipts root in Era1 archive for block {block.ToString(Block.Format.Short)}.");
        }
    }

    public Task<bool> VerifyEraFiles(string path)
    {
        var eraFiles = EraReader.GetAllEraFiles(path, "mainnet");

        throw new NotImplementedException();
    }
}
