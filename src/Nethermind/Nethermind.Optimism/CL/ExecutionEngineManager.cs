// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.CL.Derivation;

namespace Nethermind.Optimism.CL;

public class ExecutionEngineManager : IExecutionEngineManager
{
    private readonly IL2Api _l2Api;
    private readonly IDerivedBlocksVerifier _derivedBlocksVerifier;
    private readonly ILogger _logger;

    private BlockId _currentHead;
    private BlockId _currentFinalizedHead;
    private BlockId _currentSafeHead;

    public ExecutionEngineManager(IL2Api l2Api, ILogger logger)
    {
        _l2Api = l2Api;
        _logger = logger;
        _derivedBlocksVerifier = new DerivedBlocksVerifier(logger);
    }

    public async Task Initialize()
    {
        var headBlock = await _l2Api.GetHeadBlock();
        _currentHead = BlockId.FromL2Block(headBlock);

        var finalizedBlock = await _l2Api.GetFinalizedBlock();
        _currentFinalizedHead = BlockId.FromL2Block(finalizedBlock);

        var safeBlock = await _l2Api.GetSafeBlock();
        _currentSafeHead = BlockId.FromL2Block(safeBlock);

        if (_logger.IsInfo)
            _logger.Info($"EL manager initialization complete: current head {_currentHead}, current finalized head hash {_currentFinalizedHead.Hash}, current safe hash {_currentSafeHead.Hash}");
    }

    public async Task<bool> ProcessNewDerivedPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        if (_currentHead.Number >= payloadAttributes.Number)
        {
            if (_logger.IsInfo) _logger.Info($"Derived old payload. Number: {payloadAttributes.Number}");
            L2Block actualBlock = await _l2Api.GetBlockByNumber(payloadAttributes.Number);
            if (_derivedBlocksVerifier.ComparePayloadAttributes(
                    actualBlock.PayloadAttributes, payloadAttributes.PayloadAttributes, payloadAttributes.Number))
            {
                BlockId newFinalized = BlockId.FromL2Block(actualBlock);
                return await SendForkChoiceUpdated(_currentHead, newFinalized, newFinalized);
            }

            return false;
        }

        if (_logger.IsInfo) _logger.Info($"Derived payload. Number: {payloadAttributes.Number}");
        ExecutionPayloadV3? executionPayload = await BuildBlockWithPayloadAttributes(payloadAttributes);
        if (executionPayload is null)
        {
            return false;
        }

        BlockId newHead = BlockId.FromExecutionPayload(executionPayload);
        return await SendForkChoiceUpdated(newHead, newHead, newHead);
    }

    public async Task<bool> ProcessNewP2PExecutionPayload(ExecutionPayloadV3 executionPayloadV3)
    {
        return await SendNewPayload(executionPayloadV3) &&
               await SendForkChoiceUpdated(BlockId.FromExecutionPayload(executionPayloadV3), _currentFinalizedHead, _currentSafeHead);
    }

    private async Task<ExecutionPayloadV3?> BuildBlockWithPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        var fcuResult = await _l2Api.ForkChoiceUpdatedV3(
            _currentHead.Hash, _currentFinalizedHead.Hash, _currentSafeHead.Hash,
            payloadAttributes.PayloadAttributes);

        if (fcuResult.PayloadStatus.Status != PayloadStatus.Valid)
        {
            if (_logger.IsWarn) _logger.Warn($"ForkChoiceUpdated result: {fcuResult.PayloadStatus.Status}, payload number: {payloadAttributes.Number}");
            return null;
        }

        var getPayloadResult = await _l2Api.GetPayloadV3(fcuResult.PayloadId!);
        if (!await SendNewPayload(getPayloadResult.ExecutionPayload))
        {
            return null;
        }
        return getPayloadResult.ExecutionPayload;
    }

    private async Task<bool> SendNewPayload(ExecutionPayloadV3 executionPayload)
    {
        PayloadStatusV1 npResult = await _l2Api.NewPayloadV3(executionPayload, executionPayload.ParentBeaconBlockRoot);

        while (npResult.Status == PayloadStatus.Syncing)
        {
            // retry after delay
            if (_logger.IsWarn) _logger.Warn($"Got Syncing after NewPayload. {executionPayload.BlockNumber}");
            await Task.Delay(100);
            npResult = await _l2Api.NewPayloadV3(executionPayload, executionPayload.ParentBeaconBlockRoot);
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
        bool shouldUpdate = _currentHead.IsNewerThen(headBlock) ||
                            _currentFinalizedHead.IsNewerThen(finalizedBlock) ||
                            _currentSafeHead.IsNewerThen(safeBlock);

        if (!shouldUpdate)
        {
            return true;
        }

        var result = await _l2Api.ForkChoiceUpdatedV3(headBlock.Hash, finalizedBlock.Hash, safeBlock.Hash);

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
}
