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

    private ulong _currentHead = 0;

    private Hash256? _currentHeadHash;
    private Hash256? _currentFinalizedHash;
    private Hash256? _currentSafeHash;

    public ExecutionEngineManager(IL2Api l2Api, ILogger logger)
    {
        _l2Api = l2Api;
        _logger = logger;
        _derivedBlocksVerifier = new DerivedBlocksVerifier(logger);
    }

    public void Initialize()
    {
        var headBlock = _l2Api.GetHeadBlock();
        _currentHead = headBlock.Number;
        _currentHeadHash = headBlock.Hash;

        var finalizedBlock = _l2Api.GetFinalizedBlock();
        _currentFinalizedHash = finalizedBlock.Hash;
        _currentSafeHash = _currentFinalizedHash;
        _logger.Error($"EL manager initialization complete: current head {_currentHead}, current finalized head hash {_currentFinalizedHash}");
        // TODO: fix safe head
    }

    public async Task<bool> ProcessNewDerivedPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        // TODO: lock here
        if (_currentHead >= payloadAttributes.Number)
        {
            // TODO: set safe hash
            return VerifyOldPayloadAttributes(payloadAttributes);
        }

        ExecutionPayloadV3? executionPayload = await BuildBlockWithPayloadAttributes(payloadAttributes);
        if (executionPayload is null)
        {
            return false;
        }

        _currentSafeHash = executionPayload.BlockHash;

        _logger.Error($"Sending final fcu");
        return await SendForkChoiceUpdated(_currentHeadHash!, _currentFinalizedHash!, _currentSafeHash!);
    }

    public async Task<bool> ProcessNewP2PExecutionPayload(ExecutionPayloadV3 executionPayloadV3)
    {
        if (await SendNewPayload(executionPayloadV3) &&
            await SendForkChoiceUpdated(executionPayloadV3.BlockHash, _currentFinalizedHash!, _currentSafeHash!))
        {
            _currentHead = (ulong)executionPayloadV3.BlockNumber;
            return true;
        }

        return false;
    }

    private async Task<ExecutionPayloadV3?> BuildBlockWithPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        _logger.Error($"Sending fcu with pas");
        var fcuResult = await _l2Api.ForkChoiceUpdatedV3(
            _currentHeadHash!, _currentFinalizedHash!, _currentSafeHash!,
            payloadAttributes.PayloadAttributes);

        if (fcuResult.PayloadStatus.Status != PayloadStatus.Valid)
        {
            return null;
        }

        _logger.Error($"Sending getPayload");
        var getPayloadResult = await _l2Api.GetPayloadV3(fcuResult.PayloadId!);

        var executionPayload = getPayloadResult.ExecutionPayload;
        _currentHead = (ulong)executionPayload.BlockNumber;
        _currentHeadHash = executionPayload.BlockHash;

        _logger.Error($"Sending newPayload");
        if (!await SendNewPayload(executionPayload))
        {
            return null;
        }
        return executionPayload;
    }

    private bool VerifyOldPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        L2Block actualBlock = _l2Api.GetBlockByNumber(payloadAttributes.Number);
        return _derivedBlocksVerifier.ComparePayloadAttributes(actualBlock.PayloadAttributes, payloadAttributes.PayloadAttributes, payloadAttributes.Number);
    }

    private async Task<bool> SendNewPayload(ExecutionPayloadV3 executionPayload)
    {
        PayloadStatusV1 npResult = await _l2Api.NewPayloadV3(executionPayload, executionPayload.ParentBeaconBlockRoot);

        if (npResult.Status == PayloadStatus.Invalid)
        {
            if (_logger.IsTrace) _logger.Trace($"Got invalid payload");
            return false;
        }
        return true;
    }

    private async Task<bool> SendForkChoiceUpdated(Hash256 headBlockHash, Hash256 finalizedBlockHash, Hash256 safeBlockHash)
    {
        var result = await _l2Api.ForkChoiceUpdatedV3(headBlockHash, finalizedBlockHash, safeBlockHash);

        if (result.PayloadStatus.Status == PayloadStatus.Invalid)
        {
            if (_logger.IsTrace) _logger.Trace($"Invalid FCU");
            return false;
        }
        return true;
    }
}
