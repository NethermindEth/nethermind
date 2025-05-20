// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
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
    private const int L1EpochSlotSize = 32;
    private const int L1SlotTimeMilliseconds = 12000;
    private const int L1EpochTimeMilliseconds = L1EpochSlotSize * L1SlotTimeMilliseconds;

    private readonly IEthApi _ethL1Api;
    private readonly IBeaconApi _beaconApi;
    private readonly IDecodingPipeline _decodingPipeline;
    private readonly ILogger _logger;

    private BlockId _currentHead;
    private BlockId _currentFinalizedHead;

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

    public async Task<L1BridgeStepResult> Step(CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info("Starting L1Bridge");
        try
        {
            L1Block newFinalized = await GetFinalized(token);
            L1Block newHead = await GetHead(token);
            ulong newHeadNumber = newHead.Number;
            if (newHeadNumber == _currentHead.Number)
            {
                return L1BridgeStepResult.Skip;
            }

            if (!_currentFinalizedHead.IsNewerThan(newFinalized.Number))
            {
                if (_logger.IsInfo)
                    _logger.Info($"New L1 finalization signal. New finalized head: {newFinalized.Number}");
                await BuildUp(_currentHead.Number, newFinalized.Number, token); // Will process blocks if _currentHead is older than newFinalized
                _currentFinalizedHead = BlockId.FromL1Block(newFinalized);
                if (_currentHead.Number < _currentFinalizedHead.Number)
                {
                    _currentHead = _currentFinalizedHead;
                }

                await _finalizedBlocksChannel.Writer.WriteAsync(newFinalized.Number, token);
            }

            bool isReorg = await RollBack(newHead.Hash, newHeadNumber, _currentHead.Hash, _currentHead.Number, token);
            if (isReorg) return L1BridgeStepResult.Reorg;
            await ProcessBlock(newHead, token);

            _currentHead = BlockId.FromL1Block(newHead);

            await Task.Delay(L1SlotTimeMilliseconds, token);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn && e is not OperationCanceledException)
                _logger.Warn($"Unhandled exception in L1Bridge: {e}");
            throw;
        }

        return L1BridgeStepResult.Ok;
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
                        await ProcessBlobBatcherTransaction(transaction, block.Number, startingBlobIndex, slotNumber, token);
                    }

                    startingBlobIndex += transaction.BlobVersionedHashes!.Length;
                }
                else
                {
                    if (_batcherInboxAddress == transaction.To &&
                        _batchSubmitter == transaction.From)
                    {
                        await ProcessCalldataBatcherTransaction(transaction, block.Number, token);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
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

    private async Task ProcessBlobBatcherTransaction(L1Transaction transaction, ulong blockNumber, int startingBlobIndex, ulong slotNumber, CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info($"Processing Blob Batcher transaction. TxHash: {transaction.Hash}");
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
                new DaDataSource { DataOrigin = blockNumber, Data = blobSidecars[i].Blob, DataType = DaDataType.Blob }, token);
        }
    }

    private async Task ProcessCalldataBatcherTransaction(L1Transaction transaction, ulong blockNumber, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(transaction.Input);
        if (_logger.IsInfo) _logger.Info($"Processing Calldata Batcher transaction. TxHash: {transaction.Hash}");
        await _decodingPipeline.DaDataWriter.WriteAsync(
            new DaDataSource { DataOrigin = blockNumber, Data = transaction.Input, DataType = DaDataType.Calldata }, token);
    }

    /// <remarks> Processes all blocks from range ({from}, {to}). It's safe only if {to} is finalized </remarks>
    private async Task BuildUp(ulong from, ulong to, CancellationToken cancellationToken)
    {
        for (ulong i = from + 1; i < to; i++)
        {
            await ProcessBlock(await GetBlock(i, cancellationToken), cancellationToken);
        }
    }

    /// <remarks> Processes all blocks from range [{segmentStartNumber}, {headNumber}) </remarks>
    private async Task<bool> RollBack(Hash256 headHash, ulong headNumber, Hash256 segmentStartHash, ulong segmentStartNumber, CancellationToken cancellationToken)
    {
        if (headNumber <= segmentStartNumber) return false;
        Hash256 currentHash = headHash;
        L1Block[] chainSegment = new L1Block[headNumber - segmentStartNumber];
        for (ulong blockNumber = headNumber - 1; blockNumber >= segmentStartNumber; blockNumber--)
        {
            ulong i = segmentStartNumber - blockNumber;
            chainSegment[i] = await GetBlock(blockNumber, cancellationToken);
            if (currentHash != chainSegment[i].Hash)
            {
                LogReorg();
                return true;
            }
            currentHash = chainSegment[i].ParentHash;
        }

        if (currentHash != segmentStartHash)
        {
            LogReorg();
            return true;
        }

        foreach (L1Block block in chainSegment)
        {
            await ProcessBlock(block, cancellationToken);
        }
        return false;
    }

    private void LogReorg()
    {
        if (_logger.IsInfo) _logger.Info("L1 reorg detected. Resetting pipeline");
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

    public void Reset(BlockId highestFinalizedOrigin)
    {
        if (_logger.IsInfo) _logger.Info($"Resetting L1 bridge. New head: {highestFinalizedOrigin}");
        _currentHead = highestFinalizedOrigin;
        _currentFinalizedHead = highestFinalizedOrigin;
    }

    public async Task Initialize(CancellationToken token)
    {
        L1Block finalized = await GetFinalized(token);
        _currentHead = BlockId.FromL1Block(finalized);
        _currentFinalizedHead = BlockId.FromL1Block(finalized);
        if (_logger.IsInfo) _logger.Info($"Initializing L1 bridge. New head number: {finalized.Number}, new head hash {finalized.Hash}");
    }

    public async Task ProcessUntilHead(CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info("Deriving blocks after restart");
        try
        {
            L1Block newHead = await GetFinalized(token);
            while (!token.IsCancellationRequested && newHead.Number != _currentHead.Number)
            {
                await BuildUp(_currentHead.Number, newHead.Number, token);
                await ProcessBlock(newHead, token);
                await _finalizedBlocksChannel.Writer.WriteAsync(newHead.Number, token);

                _currentHead = BlockId.FromL1Block(newHead);
                newHead = await GetFinalized(token);
            }
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Exception during L1 block processing. {e}");
            throw;
        }

        if (_logger.IsInfo) _logger.Info("L1 bridge reached current head");
    }

    private readonly Channel<ulong> _finalizedBlocksChannel = Channel.CreateBounded<ulong>(L1EpochSlotSize + 1);

    public ChannelReader<ulong> FinalizedL1BlocksChannel => _finalizedBlocksChannel.Reader;
}
