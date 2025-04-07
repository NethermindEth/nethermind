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

    private readonly ICLConfig _config;
    private readonly IEthApi _ethL1Api;
    private readonly IBeaconApi _beaconApi;
    private readonly IDecodingPipeline _decodingPipeline;
    private readonly ILogger _logger;

    private ulong _currentHeadNumber = 0;
    private Hash256? _currentHeadHash = null;
    private ulong _currentFinalizedNumber;
    private Hash256? _currentFinalizedHash = null;

    private readonly Address _batchSubmitter;
    private readonly Address _batcherInboxAddress;
    private readonly ulong _l1BeaconGenesisSlotTime;

    public EthereumL1Bridge(
        IEthApi ethL1Rpc,
        IBeaconApi beaconApi,
        ICLConfig config,
        CLChainSpecEngineParameters engineParameters,
        IDecodingPipeline decodingPipeline,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(engineParameters.L1BeaconGenesisSlotTime);
        ArgumentNullException.ThrowIfNull(engineParameters.BatcherInboxAddress);
        ArgumentNullException.ThrowIfNull(engineParameters.BatchSubmitter);
        _logger = logger;
        _decodingPipeline = decodingPipeline;
        _config = config;
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

            int numberOfMissingBlocks = (int)newHeadNumber - (int)_currentHeadNumber - 1;
            if (numberOfMissingBlocks > 64)
            {
                if (_logger.IsInfo) _logger.Info(
                    $"Long head update. Number of missing blocks: {numberOfMissingBlocks}, current head number: {_currentHeadNumber}, new head number: {newHeadNumber}");
                // Try to build up instead of rolling back
                // At this point we already got blocks up until _currentHead.
                // if _currentHead was not reorged => we need blocks from _currentHead up to newHead
                // if _currentHead was reorged => we need to re-run all blocks from _currentFinalized up to newHead
                L1Block newFinalized = await GetFinalized(token);
                L1Block currentHeadBlock = await GetBlock(_currentHeadNumber, token);
                if (currentHeadBlock.Hash != _currentHeadHash)
                {
                    // Reorg currentHead
                    await BuildUp(_currentFinalizedNumber, newFinalized.Number, token);
                    await RollBack(newHead.Hash, newHeadNumber, newFinalized.Number, token);
                }
                else
                {
                    // CurrentHead is ok
                    await BuildUp(_currentHeadNumber, newFinalized.Number, token); // Will build up if _currentHead < newFinalized
                    await RollBack(newHead.Hash, newHeadNumber, _currentHeadNumber, token);
                }

                SetFinalized(newFinalized);
            }
            else if (numberOfMissingBlocks > 0)
            {
                await RollBack(newHead.Hash, newHeadNumber, _currentHeadNumber, token);
            }

            _currentHeadNumber = newHeadNumber;
            _currentHeadHash = newHead.Hash;
            await ProcessBlock(newHead, token);
            await TryUpdateFinalized(token);

            await Task.Delay(L1SlotTimeMilliseconds, token);
        }
    }

    private async Task ProcessBlock(L1Block block, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(_currentFinalizedHash);

            if (_logger.IsTrace) _logger.Trace($"New L1 Block. Number {block.Number}");
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
                        await ProcessBlobBatcherTransaction(transaction, startingBlobIndex, slotNumber, cancellationToken);
                    }

                    startingBlobIndex += transaction.BlobVersionedHashes!.Length;
                }
                else
                {
                    if (_batcherInboxAddress == transaction.To &&
                        _batchSubmitter == transaction.From)
                    {
                        ProcessCalldataBatcherTransaction(transaction);
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

    private async Task ProcessBlobBatcherTransaction(L1Transaction transaction, int startingBlobIndex, ulong slotNumber, CancellationToken cancellationToken)
    {
        BlobSidecar[] blobSidecars = await _beaconApi.GetBlobSidecars(slotNumber, startingBlobIndex,
            startingBlobIndex + transaction.BlobVersionedHashes!.Length - 1, cancellationToken);

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
            await _decodingPipeline.DaDataWriter.WriteAsync(blobSidecars[i].Blob, cancellationToken);
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

    private async Task TryUpdateFinalized(CancellationToken token)
    {
        if (_currentHeadNumber - _currentFinalizedNumber >= 64)
        {
            L1Block newFinalized = await GetFinalized(token);
            SetFinalized(newFinalized);
        }
    }

    private void SetFinalized(L1Block newFinalized)
    {
        if (newFinalized.Hash != _currentFinalizedHash)
        {
            if (_logger.IsInfo) _logger.Info($"New finalized head signal. Number: {newFinalized.Number}, Hash: {newFinalized.Hash}");
            _currentFinalizedHash = newFinalized.Hash;
            _currentFinalizedNumber = newFinalized.Number;
        }
    }

    // Gets all blocks from range [{segmentStartNumber}, {headNumber})
    private async Task RollBack(Hash256 headHash, ulong headNumber, ulong segmentStartNumber, CancellationToken cancellationToken)
    {
        Hash256 currentHash = headHash;
        L1Block[] chainSegment = new L1Block[headNumber - segmentStartNumber + 1];
        for (int i = chainSegment.Length - 1; i >= 0; i--)
        {
            _logger.Info($"Rolling back L1 head. {i} to go. Block {currentHash}");
            chainSegment[i] = await GetBlockByHash(currentHash, cancellationToken);
            currentHash = chainSegment[i].ParentHash;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        for (int i = 0; i < chainSegment.Length; i++)
        {
            await ProcessBlock(chainSegment[i], cancellationToken);
        }
    }

    // Gets all blocks from range ({from}, {to}). It's safe only if {to} is finalized
    private async Task BuildUp(ulong from, ulong to, CancellationToken cancellationToken)
    {
        for (ulong i = from + 1; i < to; i++)
        {
            await ProcessBlock(await GetBlock(i, cancellationToken), cancellationToken);
        }
    }

    public async Task<L1Block> GetBlock(ulong blockNumber, CancellationToken token)
    {
        L1Block? result = await _ethL1Api.GetBlockByNumber(blockNumber, true);
        while (result is null)
        {
            token.ThrowIfCancellationRequested();
            if (_logger.IsWarn) _logger.Warn($"Unable to get L1 block by block number({blockNumber})");
            result = await _ethL1Api.GetBlockByNumber(blockNumber, true);
        }

        return result.Value;
    }

    public async Task<L1Block> GetBlockByHash(Hash256 blockHash, CancellationToken token)
    {
        L1Block? result = await _ethL1Api.GetBlockByHash(blockHash, true);
        while (result is null)
        {
            token.ThrowIfCancellationRequested();
            if (_logger.IsWarn) _logger.Warn($"Unable to get L1 block by hash({blockHash})");
            result = await _ethL1Api.GetBlockByHash(blockHash, true);
        }

        return result.Value;
    }

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

    private async Task<L1Block> GetHead(CancellationToken token)
    {
        L1Block? result = await _ethL1Api.GetHead(true);
        while (result is null)
        {
            token.ThrowIfCancellationRequested();
            _logger.Warn($"Unable to get L1 head");
            result = await _ethL1Api.GetHead(true);
        }

        return result.Value;
    }

    private async Task<L1Block> GetFinalized(CancellationToken token)
    {
        L1Block? result = await _ethL1Api.GetFinalized(true);
        while (result is null)
        {
            token.ThrowIfCancellationRequested();
            _logger.Warn($"Unable to get finalized L1 block");
            result = await _ethL1Api.GetFinalized(true);
        }

        return result.Value;
    }

    public void Reset(L1BlockInfo highestFinalizedOrigin)
    {
        if (_logger.IsInfo) _logger.Info($"Resetting L1 bridge. New head number: {highestFinalizedOrigin.Number}, new head hash {highestFinalizedOrigin.BlockHash}");
        _currentFinalizedNumber = highestFinalizedOrigin.Number;
        _currentHeadNumber = highestFinalizedOrigin.Number;
        _currentHeadHash = highestFinalizedOrigin.BlockHash;
        _currentFinalizedHash = highestFinalizedOrigin.BlockHash;
    }
}
