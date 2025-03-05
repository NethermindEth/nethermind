// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;

namespace Nethermind.Optimism.CL.L1Bridge;

public class EthereumL1Bridge : IL1Bridge
{
    private readonly ICLConfig _config;
    private readonly CLChainSpecEngineParameters _engineParameters;
    private readonly IEthApi _ethL1Api;
    private readonly IBeaconApi _beaconApi;
    private readonly IDecodingPipeline _decodingPipeline;
    private readonly ILogger _logger;

    private ulong _currentHeadNumber = 0;
    private Hash256? _currentHeadHash = null;
    private ulong _currentFinalizedNumber;
    private Hash256? _currentFinalizedHash = null;

    public EthereumL1Bridge(
        IEthApi ethL1Rpc,
        IBeaconApi beaconApi,
        ICLConfig config,
        CLChainSpecEngineParameters engineParameters,
        IDecodingPipeline decodingPipeline,
        ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _engineParameters = engineParameters;
        _decodingPipeline = decodingPipeline;
        _config = config;
        _ethL1Api = ethL1Rpc;
        _beaconApi = beaconApi;
    }

    public async Task Run(CancellationToken token)
    {
        _logger.Error("Starting L1Bridge");
        while (!token.IsCancellationRequested)
        {
            // TODO: can we do it with subscription?
            L1Block newHead = await GetHead();
            ulong newHeadNumber = newHead.Number;

            int numberOfMissingBlocks = (int)newHeadNumber - (int)_currentHeadNumber - 1;
            if (numberOfMissingBlocks > 64)
            {
                _logger.Error(
                    $"Long head update. Number of missing blocks: {numberOfMissingBlocks}, current head number: {_currentHeadNumber}, new head number: {newHeadNumber}");
                // Try to build up instead of rolling back
                // At this point we already got blocks up until _currentHead.
                // if _currentHead was not reorged => we need blocks from _currentHead up to newHead
                // if _currentHead was reorged => we need to re-run all blocks from _currentFinalized up to newHead
                L1Block newFinalized = await GetFinalized();
                L1Block currentHeadBlock = await GetBlock(_currentHeadNumber);
                if (currentHeadBlock.Hash != _currentHeadHash)
                {
                    // Reorg currentHead
                    await BuildUp(_currentFinalizedNumber, newFinalized.Number);
                    await RollBack(newHead.Hash, newHeadNumber, newFinalized.Number, token);
                }
                else
                {
                    // CurrentHead is ok
                    await BuildUp(_currentHeadNumber, newFinalized.Number); // Will build up if _currentHead < newFinalized
                    await RollBack(newHead.Hash, newHeadNumber, _currentHeadNumber, token);
                }

                SetFinalized(newFinalized);
            }
            else if (numberOfMissingBlocks > 0)
            {
                await RollBack(newHead.Hash, newHeadNumber, _currentHeadNumber, token);
            }

            // TODO we can have reorg here
            _currentHeadNumber = newHeadNumber;
            _currentHeadHash = newHead.Hash;
            await ProcessBlock(newHead);
            await TryUpdateFinalized();

            // TODO: fix number
            await Task.Delay(50000, token);
        }
    }

    private async Task ProcessBlock(L1Block block)
    {
        ArgumentNullException.ThrowIfNull(_currentFinalizedHash);

        _logger.Error($"New L1 Block. Number {block.Number}");
        int startingBlobIndex = 0;
        // Filter batch submitter transaction
        foreach (L1Transaction transaction in block.Transactions!)
        {
            if (transaction.Type == TxType.Blob)
            {
                if (_engineParameters.BatcherInboxAddress == transaction.To &&
                    _engineParameters.BatcherAddress == transaction.From)
                {
                    ulong slotNumber = CalculateSlotNumber(block.Timestamp.ToUInt64(null));
                    await ProcessBlobBatcherTransaction(transaction,
                        startingBlobIndex, slotNumber);
                }
                startingBlobIndex += transaction.BlobVersionedHashes!.Length;
            }
            else
            {
                if (_engineParameters.BatcherInboxAddress == transaction.To &&
                    _engineParameters.BatcherAddress == transaction.From)
                {
                    ProcessCalldataBatcherTransaction(transaction);
                }
            }
        }
    }

    private ulong CalculateSlotNumber(ulong timestamp)
    {
        // TODO: review
        const ulong beaconGenesisTimestamp = 1606824023;
        const ulong l1SlotTime = 12;
        return (timestamp - beaconGenesisTimestamp) / l1SlotTime;
    }

    private async Task ProcessBlobBatcherTransaction(L1Transaction transaction, int startingBlobIndex, ulong slotNumber)
    {
        BlobSidecar[] blobSidecars = await GetBlobSidecars(slotNumber, startingBlobIndex,
            startingBlobIndex + transaction.BlobVersionedHashes!.Length);

        for (int i = 0; i < transaction.BlobVersionedHashes.Length; i++)
        {
            await _decodingPipeline.DaDataWriter.WriteAsync(blobSidecars[i].Blob);
        }
    }

    private void ProcessCalldataBatcherTransaction(L1Transaction transaction)
    {
        if (_logger.IsError)
        {
            _logger.Error($"GOT REGULAR TRANSACTION");
        }

        throw new NotImplementedException();
    }

    private async Task TryUpdateFinalized()
    {
        if (_currentHeadNumber - _currentFinalizedNumber >= 64)
        {
            L1Block newFinalized = await GetFinalized();
            SetFinalized(newFinalized);
        }
    }

    private void SetFinalized(L1Block newFinalized)
    {
        if (newFinalized.Hash != _currentFinalizedHash)
        {
            _logger.Error($"New finalized head signal. Number: {newFinalized.Number}, Hash: {newFinalized.Hash}");
            _currentFinalizedHash = newFinalized.Hash;
            _currentFinalizedNumber = newFinalized.Number;
        }
    }

    // Gets all blocks from range [{segmentStartNumber}, {headNumber})
    private async Task RollBack(Hash256 headHash, ulong headNumber, ulong segmentStartNumber, CancellationToken token)
    {
        Hash256 currentHash = headHash;
        L1Block[] chainSegment = new L1Block[headNumber - segmentStartNumber + 1];
        for (int i = chainSegment.Length - 1; i >= 0; i--)
        {
            _logger.Info($"Rolling back L1 head. {i} to go. Block {currentHash}");
            chainSegment[i] = await GetBlockByHash(currentHash);
            currentHash = chainSegment[i].ParentHash;
            if (token.IsCancellationRequested)
            {
                return;
            }
        }

        if (_currentHeadHash is not null && _currentHeadHash != chainSegment[0].ParentHash)
        {
            // TODO: chain reorg
        }

        for (int i = 0; i < chainSegment.Length; i++)
        {
            await ProcessBlock(chainSegment[i]);
        }
    }

    // Gets all blocks from range ({from}, {to}). It's safe only if {to} is finalized
    private async Task BuildUp(ulong from, ulong to)
    {
        for (ulong i = from + 1; i < to; i++)
        {
            await ProcessBlock(await GetBlock(i));
        }
    }

    public Task<BlobSidecar[]> GetBlobSidecars(ulong slotNumber, int indexFrom, int indexTo)
    {
        // TODO: retry here
        return _beaconApi.GetBlobSidecars(slotNumber, indexFrom, indexTo)!;
    }

    public async Task<L1Block> GetBlock(ulong blockNumber)
    {
        L1Block? result = await _ethL1Api.GetBlockByNumber(blockNumber, true);
        while (result is null)
        {
            _logger.Warn($"Unable to get L1 block by block number({blockNumber})");
            result = await _ethL1Api.GetBlockByNumber(blockNumber, true);
        }

        CacheBlock(result.Value);
        return result.Value;
    }

    // TODO: pruning
    private readonly ConcurrentDictionary<Hash256, L1Block> _cachedL1Blocks = new();
    private readonly ConcurrentDictionary<Hash256, ReceiptForRpc[]> _cachedReceipts = new();

    private void CacheBlock(L1Block block)
    {
        _cachedL1Blocks.TryAdd(block.Hash, block);
    }

    private void CacheReceipts(Hash256 blockHash, ReceiptForRpc[] receipts)
    {
        _cachedReceipts.TryAdd(blockHash, receipts);
    }

    public async Task<L1Block> GetBlockByHash(Hash256 blockHash)
    {
        if (_cachedL1Blocks.TryGetValue(blockHash, out L1Block cachedBlock))
        {
            return cachedBlock;
        }

        L1Block? result = await _ethL1Api.GetBlockByHash(blockHash, true);
        while (result is null)
        {
            _logger.Warn($"Unable to get L1 block by hash({blockHash})");
            result = await _ethL1Api.GetBlockByHash(blockHash, true);
        }

        CacheBlock(result.Value);
        return result.Value;
    }

    public async Task<ReceiptForRpc[]> GetReceiptsByBlockHash(Hash256 blockHash)
    {
        if (_cachedReceipts.TryGetValue(blockHash, out ReceiptForRpc[]? cachedReceipts))
        {
            return cachedReceipts;
        }

        ReceiptForRpc[]? result = await _ethL1Api.GetReceiptsByHash(blockHash);
        while (result is null)
        {
            _logger.Warn($"Unable to get L1 receipts by hash({blockHash})");
            result = await _ethL1Api.GetReceiptsByHash(blockHash);
        }

        CacheReceipts(blockHash, result);
        return result;
    }

    private async Task<L1Block> GetHead()
    {
        L1Block? result = await _ethL1Api.GetHead(true);
        while (result is null)
        {
            _logger.Warn($"Unable to get L1 head");
            result = await _ethL1Api.GetHead(true);
        }

        CacheBlock(result.Value);
        return result.Value;
    }

    private async Task<L1Block> GetFinalized()
    {
        L1Block? result = await _ethL1Api.GetFinalized(true);
        while (result is null)
        {
            _logger.Warn($"Unable to get finalized L1 block");
            result = await _ethL1Api.GetFinalized(true);
        }

        CacheBlock(result.Value);
        return result.Value;
    }

    public void SetCurrentL1Head(ulong blockNumber, Hash256 blockHash)
    {
        _logger.Error($"Setting current L1 head. {blockNumber}({blockHash})");
        _currentFinalizedNumber = blockNumber;
        _currentHeadNumber = blockNumber;
        _currentHeadHash = blockHash;
        _currentFinalizedHash = blockHash;
    }
}
