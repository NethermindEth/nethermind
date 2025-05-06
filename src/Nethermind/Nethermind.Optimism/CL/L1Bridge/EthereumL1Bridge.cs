// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;

namespace Nethermind.Optimism.CL.L1Bridge;

public class EthereumL1Bridge : IL1Bridge
{
    private const int L1SlotTimeMilliseconds = 12000;

    private readonly IEthApi _ethL1Api;
    private readonly IBeaconApi _beaconApi;
    private readonly IDecodingPipeline _decodingPipeline;
    private readonly ILogger _logger;

    private BlockId _currentHead;

    private readonly Address _batchSubmitter;
    private readonly Address _batcherInboxAddress;
    private readonly ulong _l1BeaconGenesisSlotTime;

    public EthereumL1Bridge(
        IEthApi ethL1Rpc,
        IBeaconApi beaconApi,
        CLChainSpecEngineParameters engineParameters,
        IDecodingPipeline decodingPipeline,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(engineParameters.L1BeaconGenesisSlotTime);
        ArgumentNullException.ThrowIfNull(engineParameters.BatcherInboxAddress);
        ArgumentNullException.ThrowIfNull(engineParameters.BatchSubmitter);
        _logger = logger;
        _decodingPipeline = decodingPipeline;
        _ethL1Api = ethL1Rpc;
        _beaconApi = beaconApi;

        _batchSubmitter = engineParameters.BatchSubmitter;
        _batcherInboxAddress = engineParameters.BatcherInboxAddress;
        _l1BeaconGenesisSlotTime = engineParameters.L1BeaconGenesisSlotTime.Value;
    }

    public async Task Run(CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info("Starting L1Bridge");
        while (!token.IsCancellationRequested)
        {
            L1Block newHead = await GetFinalized(token);
            ulong newHeadNumber = newHead.Number;
            if (newHeadNumber == _currentHead.Number)
            {
                await Task.Delay(1000, token);
                continue;
            }
            await BuildUp(_currentHead.Number, newHeadNumber, token);
            await ProcessBlock(newHead, token);

            _currentHead = BlockId.FromL1Block(newHead);

            await Task.Delay(32 * L1SlotTimeMilliseconds, token);
        }
    }

    private async Task ProcessBlock(L1Block block, CancellationToken token)
    {
        try
        {
            if (_logger.IsInfo) _logger.Info($"New L1 Block. Number {block.Number}");
            int startingBlobIndex = 0;
            // Filter batch submitter transaction
            foreach (L1Transaction transaction in block.Transactions!)
            {
                if (transaction.Type == TxType.Blob)
                {
                    if (_batcherInboxAddress == transaction.To &&
                        _batchSubmitter == transaction.From)
                    {
                        ulong slotNumber = CalculateSlotNumber(block.Timestamp.ToUInt64(null));
                        await ProcessBlobBatcherTransaction(transaction, startingBlobIndex, slotNumber, token);
                    }

                    startingBlobIndex += transaction.BlobVersionedHashes!.Length;
                }
                else
                {
                    if (_batcherInboxAddress == transaction.To &&
                        _batchSubmitter == transaction.From)
                    {
                        await ProcessCalldataBatcherTransaction(transaction, token);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"Processing block failed: {ex.Message}");
            throw;
        }
    }

    private ulong CalculateSlotNumber(ulong timestamp)
    {
        const ulong l1SlotTime = 12;
        return (timestamp - _l1BeaconGenesisSlotTime) / l1SlotTime;
    }

    private async Task ProcessBlobBatcherTransaction(L1Transaction transaction, int startingBlobIndex, ulong slotNumber, CancellationToken token)
    {
        BlobSidecar[] blobSidecars = await _beaconApi.GetBlobSidecars(slotNumber, startingBlobIndex,
            startingBlobIndex + transaction.BlobVersionedHashes!.Length - 1, token);

        if (blobSidecars is null)
        {
            if (_logger.IsWarn)
            {
                string blobVersionedHashes = string.Join(',', transaction.BlobVersionedHashes.Select(hash => hash.ToHexString()));
                _logger.Warn($"Failed to get blob sidecars for slot {slotNumber}. Transaction: {transaction.Hash}. BlobVersionedHashes: {blobVersionedHashes}");
            }

            throw new ArgumentNullException(nameof(blobSidecars));
        }

        for (int i = 0; i < transaction.BlobVersionedHashes.Length; i++)
        {
            await _decodingPipeline.DaDataWriter.WriteAsync(
                new DaDataSource { Data = blobSidecars[i].Blob, DataType = DaDataType.Blob }, token);
        }
    }

    private async Task ProcessCalldataBatcherTransaction(L1Transaction transaction, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(transaction.Input);
        if (_logger.IsInfo) _logger.Info($"Processing Calldata Batcher transaction. TxHash: {transaction.Hash}");
        await _decodingPipeline.DaDataWriter.WriteAsync(
            new DaDataSource { Data = transaction.Input, DataType = DaDataType.Calldata }, token);
    }

    /// <remarks> Gets all blocks from range ({from}, {to}). It's safe only if {to} is finalized </remarks>
    private async Task BuildUp(ulong from, ulong to, CancellationToken cancellationToken)
    {
        for (ulong i = from + 1; i < to; i++)
        {
            await ProcessBlock(await GetBlock(i, cancellationToken), cancellationToken);
        }
    }

    public async Task<L1Block> GetBlock(ulong blockNumber, CancellationToken token) =>
        await RetryGetBlock(async () => await _ethL1Api.GetBlockByNumber(blockNumber, true), token);

    public async Task<L1Block> GetBlockByHash(Hash256 blockHash, CancellationToken token) =>
        await RetryGetBlock(async () => await _ethL1Api.GetBlockByHash(blockHash, true), token);

    public async Task<ReceiptForRpc[]> GetReceiptsByBlockHash(Hash256 blockHash, CancellationToken token)
    {
        ReceiptForRpc[]? result = await _ethL1Api.GetReceiptsByHash(blockHash);
        while (result is null)
        {
            token.ThrowIfCancellationRequested();
            if (_logger.IsWarn) _logger.Warn($"Unable to get L1 receipts by hash({blockHash})");
            result = await _ethL1Api.GetReceiptsByHash(blockHash);
        }

        return result;
    }

    private async Task<L1Block> GetHead(CancellationToken token) =>
        await RetryGetBlock(async () => await _ethL1Api.GetHead(true), token);

    private async Task<L1Block> GetFinalized(CancellationToken token) =>
        await RetryGetBlock(async () => await _ethL1Api.GetFinalized(true), token);

    private async Task<L1Block> RetryGetBlock(Func<Task<L1Block?>> getBlock, CancellationToken token)
    {
        L1Block? result = await getBlock();
        while (result is null)
        {
            token.ThrowIfCancellationRequested();
            if (_logger.IsWarn) _logger.Warn($"Unable to get L1 block.");
            result = await getBlock();
        }
        return result.Value;
    }

    public void Reset(L1BlockInfo highestFinalizedOrigin)
    {
        if (_logger.IsInfo) _logger.Info($"Resetting L1 bridge. New head number: {highestFinalizedOrigin.Number}, new head hash {highestFinalizedOrigin.BlockHash}");
        _currentHead = BlockId.FromL1BlockInfo(highestFinalizedOrigin);
    }

    public async Task Initialize(CancellationToken token)
    {
        L1Block finalized = await GetFinalized(token);
        _currentHead = BlockId.FromL1Block(finalized);
        if (_logger.IsInfo) _logger.Info($"Initializing L1 bridge. New head number: {finalized.Number}, new head hash {finalized.Hash}");
    }
}
