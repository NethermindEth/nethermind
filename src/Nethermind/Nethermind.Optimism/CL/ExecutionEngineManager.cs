// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.CL.Derivation;

namespace Nethermind.Optimism.CL;

public class ExecutionEngineManager(
    IL2Api l2Api,
    ILogger logger) : IExecutionEngineManager
{
    private readonly IDerivedBlocksVerifier _derivedBlocksVerifier = new DerivedBlocksVerifier(logger);

    private BlockId _currentHead;
    private BlockId _currentFinalizedHead;
    private BlockId _currentSafeHead;

    public async Task Initialize()
    {
        var headBlock = await l2Api.GetHeadBlock();
        var finalizedBlock = await l2Api.GetFinalizedBlock();
        var safeBlock = await l2Api.GetSafeBlock();

        _currentHead = BlockId.FromL2Block(headBlock);
        _currentFinalizedHead = BlockId.FromL2Block(finalizedBlock);
        _currentSafeHead = BlockId.FromL2Block(safeBlock);

        if (logger.IsInfo)
            logger.Info($"EL manager initialization complete: current head {_currentHead}, current finalized head hash {_currentFinalizedHead.Hash}, current safe hash {_currentSafeHead.Hash}");
    }

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<bool> ProcessNewDerivedPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_currentHead.Number >= payloadAttributes.Number)
            {
                if (logger.IsInfo) logger.Info($"Derived old payload. Number: {payloadAttributes.Number}");
                L2Block actualBlock = await l2Api.GetBlockByNumber(payloadAttributes.Number);
                if (_derivedBlocksVerifier.ComparePayloadAttributes(
                        actualBlock.PayloadAttributes, payloadAttributes.PayloadAttributes, payloadAttributes.Number))
                {
                    BlockId newFinalized = BlockId.FromL2Block(actualBlock);
                    return await SendForkChoiceUpdated(_currentHead, newFinalized, newFinalized);
                }

                return false;
            }

            if (logger.IsInfo) logger.Info($"Derived payload. Number: {payloadAttributes.Number}");
            ExecutionPayloadV3? executionPayload = await BuildBlockWithPayloadAttributes(payloadAttributes);
            if (executionPayload is null)
            {
                return false;
            }

            BlockId newHead = BlockId.FromExecutionPayload(executionPayload);
            return await SendForkChoiceUpdated(newHead, newHead, newHead);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> ProcessNewP2PExecutionPayload(ExecutionPayloadV3 executionPayload)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_currentHead.Number >= (ulong)executionPayload.BlockNumber)
            {
                if (logger.IsTrace) logger.Trace($"Got old P2P payload. Number: {executionPayload.BlockNumber}");
                return true;
            }

            if (logger.IsInfo)
                logger.Info(
                    $"New P2P Execution Payload. {executionPayload.BlockNumber} ({executionPayload.BlockHash})");
            PayloadStatusV1 npResult =
                await l2Api.NewPayloadV3(executionPayload, executionPayload.ParentBeaconBlockRoot);
            switch (npResult.Status)
            {
                case PayloadStatus.Invalid:
                    {
                        if (logger.IsWarn) logger.Warn($"Got invalid P2P payload. {executionPayload}");
                        return false;
                    }
                case PayloadStatus.Valid:
                    {
                        if (logger.IsTrace) logger.Trace($"NewPayload Valid P2P payload. {executionPayload}");
                        break;
                    }
                case PayloadStatus.Accepted:
                    {
                        if (logger.IsTrace) logger.Trace($"Accepted P2P payload. {executionPayload}");
                        break;
                    }
                case PayloadStatus.Syncing:
                    {
                        if (logger.IsTrace) logger.Trace($"Syncing P2P payload. {executionPayload}");
                        break;
                    }
            }

            var fcuResult = await l2Api.ForkChoiceUpdatedV3(executionPayload.BlockHash, _currentFinalizedHead.Hash,
                _currentSafeHead.Hash);
            switch (fcuResult.PayloadStatus.Status)
            {
                case PayloadStatus.Invalid:
                    {
                        if (logger.IsWarn) logger.Warn($"Got invalid P2P payload. {executionPayload}");
                        return false;
                    }
                case PayloadStatus.Valid:
                    {
                        if (logger.IsInfo) logger.Info($"FCU Valid P2P payload. {executionPayload}");
                        _currentHead = BlockId.FromExecutionPayload(executionPayload);
                        if (!OnELSynced.IsCompleted)
                        {
                            if (logger.IsTrace) logger.Trace("EL sync completed");
                            _elSyncedTaskCompletionSource.SetResult();
                        }

                        break;
                    }
                case PayloadStatus.Accepted:
                    {
                        if (logger.IsInfo) logger.Info($"FCU Accepted P2P payload. {executionPayload}");
                        break;
                    }
                case PayloadStatus.Syncing:
                    {
                        if (logger.IsInfo) logger.Info($"FCU Syncing P2P payload. {executionPayload}");
                        break;
                    }
            }

            return true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<ExecutionPayloadV3?> BuildBlockWithPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        var fcuResult = await l2Api.ForkChoiceUpdatedV3(
            _currentHead.Hash, _currentFinalizedHead.Hash, _currentSafeHead.Hash,
            payloadAttributes.PayloadAttributes);

        if (fcuResult.PayloadStatus.Status != PayloadStatus.Valid)
        {
            if (logger.IsWarn) logger.Warn($"ForkChoiceUpdated result: {fcuResult.PayloadStatus.Status}, payload number: {payloadAttributes.Number}");
            return null;
        }

        var getPayloadResult = await l2Api.GetPayloadV3(fcuResult.PayloadId!);
        if (!await SendNewPayload(getPayloadResult.ExecutionPayload))
        {
            return null;
        }
        return getPayloadResult.ExecutionPayload;
    }

    private async Task<bool> SendNewPayload(ExecutionPayloadV3 executionPayload)
    {
        PayloadStatusV1 npResult = await l2Api.NewPayloadV3(executionPayload, executionPayload.ParentBeaconBlockRoot);

        while (npResult.Status == PayloadStatus.Syncing)
        {
            // retry after delay
            if (logger.IsWarn) logger.Warn($"Got Syncing after NewPayload. {executionPayload.BlockNumber}");
            await Task.Delay(100);
            npResult = await l2Api.NewPayloadV3(executionPayload, executionPayload.ParentBeaconBlockRoot);
        }

        if (npResult.Status != PayloadStatus.Valid)
        {
            if (logger.IsWarn) logger.Warn($"NewPayloadV3 result: {npResult.Status}, payload number: {executionPayload.BlockNumber}");
            return false;
        }
        return true;
    }

    private async Task<bool> SendForkChoiceUpdated(
        BlockId headBlock, BlockId finalizedBlock, BlockId safeBlock)
    {
        bool shouldUpdate = _currentHead.IsNewerThan(headBlock) ||
                            _currentFinalizedHead.IsNewerThan(finalizedBlock) ||
                            _currentSafeHead.IsNewerThan(safeBlock);

        if (!shouldUpdate)
        {
            return true;
        }

        var result = await l2Api.ForkChoiceUpdatedV3(headBlock.Hash, finalizedBlock.Hash, safeBlock.Hash);

        if (result.PayloadStatus.Status != PayloadStatus.Valid)
        {
            if (logger.IsWarn) logger.Warn($"Invalid ForkChoiceUpdatedV3({headBlock.Hash}, {finalizedBlock.Hash}, {safeBlock.Hash}), Result: {result.PayloadStatus.Status}");
            return false;
        }
        _currentHead = headBlock;
        _currentFinalizedHead = finalizedBlock;
        _currentSafeHead = safeBlock;

        return true;
    }
    public Task<ulong?> GetCurrentFinalizedBlockNumber()
    {
        return Task.FromResult(_currentFinalizedHead.Number != 0 ? _currentFinalizedHead.Number : (ulong?)null);
    }

    private readonly TaskCompletionSource _elSyncedTaskCompletionSource = new();
    public Task OnELSynced => _elSyncedTaskCompletionSource.Task;
}
