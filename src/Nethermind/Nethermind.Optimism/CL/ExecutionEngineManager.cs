// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.CL.Derivation;

namespace Nethermind.Optimism.CL;

public class ExecutionEngineManager(
    IL2Api l2Api,
    ILogManager logManager) : IExecutionEngineManager
{
    private readonly DerivedBlocksVerifier _derivedBlocksVerifier = new(logManager);
    private readonly ILogger _logger = logManager.GetClassLogger();

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

        if (_logger.IsInfo)
            _logger.Info($"EL manager initialization complete: current head {_currentHead}, current finalized head hash {_currentFinalizedHead.Hash}, current safe hash {_currentSafeHead.Hash}");
    }

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<BlockId?> ProcessNewDerivedPayloadAttributes(PayloadAttributesRef payloadAttributes, CancellationToken token)
    {
        await _semaphore.WaitAsync(token);
        try
        {
            if (_currentHead.Number >= payloadAttributes.Number)
            {
                if (_logger.IsInfo) _logger.Info($"Derived old payload. Number: {payloadAttributes.Number}");
                L2Block actualBlock = await l2Api.GetBlockByNumber(payloadAttributes.Number);
                if (_derivedBlocksVerifier.ComparePayloadAttributes(
                        actualBlock.PayloadAttributes, payloadAttributes.PayloadAttributes, payloadAttributes.Number))
                {
                    BlockId newSafe = BlockId.FromL2Block(actualBlock);
                    return await SendForkChoiceUpdated(_currentHead, _currentFinalizedHead, newSafe) ? newSafe : null;
                }

                return null;
            }

            if (_logger.IsInfo) _logger.Info($"Derived payload. Number: {payloadAttributes.Number}");
            ExecutionPayloadV3? executionPayload = await BuildBlockWithPayloadAttributes(payloadAttributes);
            if (executionPayload is null)
            {
                return null;
            }

            BlockId newHead = BlockId.FromExecutionPayload(executionPayload);
            return await SendForkChoiceUpdated(newHead, _currentFinalizedHead, newHead) ? newHead : null;
        }
        finally
        {
            _semaphore.Release();
        }
    }
    public async Task<bool> FinalizeBlock(BlockId finalizedBlock, CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info($"Finalizing L2 Block {finalizedBlock}");
        await _semaphore.WaitAsync(token);
        try
        {
            return await SendForkChoiceUpdated(_currentHead, finalizedBlock, _currentSafeHead);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Exception during block finalization: {e}");
        }
        finally
        {
            _semaphore.Release();
        }

        return false;
    }

    public async Task<P2PPayloadStatus> ProcessNewP2PExecutionPayload(ExecutionPayloadV3 executionPayload, CancellationToken token)
    {
        await _semaphore.WaitAsync(token);
        try
        {
            if (_currentHead.Number >= (ulong)executionPayload.BlockNumber)
            {
                if (_logger.IsTrace) _logger.Trace($"Got old P2P payload. Number: {executionPayload.BlockNumber}");
                return P2PPayloadStatus.Valid;
            }

            if (_logger.IsInfo)
                _logger.Info(
                    $"New P2P Execution Payload. {executionPayload.BlockNumber} ({executionPayload.BlockHash})");
            PayloadStatusV1 npResult =
                await l2Api.NewPayloadV3(executionPayload, executionPayload.ParentBeaconBlockRoot);
            switch (npResult.Status)
            {
                case PayloadStatus.Invalid:
                    {
                        if (_logger.IsWarn) _logger.Warn($"Invalid P2P payload. {executionPayload}");
                        return P2PPayloadStatus.Invalid;
                    }
                case PayloadStatus.Valid:
                    {
                        if (_logger.IsInfo) _logger.Info($"New Payload Valid. {executionPayload}");
                        break;
                    }
                case PayloadStatus.Accepted:
                    {
                        if (_logger.IsInfo) _logger.Info($"New Payload Accepted. {executionPayload}");
                        break;
                    }
                case PayloadStatus.Syncing:
                    {
                        if (_logger.IsInfo) _logger.Info($"New Payload Syncing. {executionPayload}");
                        break;
                    }
            }

            var fcuResult = await l2Api.ForkChoiceUpdatedV3(executionPayload.BlockHash, _currentFinalizedHead.Hash,
                _currentSafeHead.Hash);
            switch (fcuResult.PayloadStatus.Status)
            {
                case PayloadStatus.Invalid:
                    {
                        if (_logger.IsWarn) _logger.Warn($"Got invalid P2P payload. {executionPayload}");
                        return P2PPayloadStatus.Invalid;
                    }
                case PayloadStatus.Valid:
                    {
                        if (_logger.IsInfo) _logger.Info($"FCU Valid P2P payload. {executionPayload}");
                        _currentHead = BlockId.FromExecutionPayload(executionPayload);
                        if (!OnELSynced.IsCompleted)
                        {
                            if (_logger.IsTrace) _logger.Trace("EL sync completed");
                            _elSyncedTaskCompletionSource.SetResult();
                        }

                        return P2PPayloadStatus.Valid;
                    }
                case PayloadStatus.Syncing:
                    {
                        if (_logger.IsInfo) _logger.Info($"FCU Syncing P2P payload. {executionPayload}");
                        return P2PPayloadStatus.Syncing;
                    }
                default:
                    {
                        if (_logger.IsWarn)
                            _logger.Warn($"Unexpected Payload Status({fcuResult.PayloadStatus.Status}). While processing {executionPayload}");
                        return P2PPayloadStatus.Invalid;
                    }
            }
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
            if (_logger.IsWarn) _logger.Warn($"ForkChoiceUpdated result: {fcuResult.PayloadStatus.Status}, payload number: {payloadAttributes.Number}");
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
            if (_logger.IsWarn) _logger.Warn($"Got Syncing after NewPayload. {executionPayload.BlockNumber}");
            await Task.Delay(100);
            npResult = await l2Api.NewPayloadV3(executionPayload, executionPayload.ParentBeaconBlockRoot);
        }

        if (npResult.Status != PayloadStatus.Valid)
        {
            if (_logger.IsWarn) _logger.Warn($"NewPayloadV3 result: {npResult.Status}, payload number: {executionPayload.BlockNumber}");
            return false;
        }
        return true;
    }

    private async Task<bool> SendForkChoiceUpdated(
        BlockId headBlock, BlockId finalizedBlock, BlockId safeBlock)
    {
        bool shouldUpdate = _currentHead.IsOlderThan(headBlock) ||
                            _currentFinalizedHead.IsOlderThan(finalizedBlock) ||
                            _currentSafeHead.IsOlderThan(safeBlock);

        if (!shouldUpdate)
        {
            return true;
        }

        var result = await l2Api.ForkChoiceUpdatedV3(headBlock.Hash, finalizedBlock.Hash, safeBlock.Hash);

        if (result.PayloadStatus.Status != PayloadStatus.Valid)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid ForkChoiceUpdatedV3({headBlock.Hash}, {finalizedBlock.Hash}, {safeBlock.Hash}), Result: {result.PayloadStatus.Status}");
            return false;
        }
        _currentHead = headBlock;
        _currentFinalizedHead = finalizedBlock;
        _currentSafeHead = safeBlock;

        return true;
    }

    public async Task<(BlockId Head, BlockId Finalized, BlockId Safe)> GetCurrentBlocks()
    {
        await _semaphore.WaitAsync();
        try
        {
            return (_currentHead, _currentFinalizedHead, _currentSafeHead);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private readonly TaskCompletionSource _elSyncedTaskCompletionSource = new();
    public Task OnELSynced => _elSyncedTaskCompletionSource.Task;
}
