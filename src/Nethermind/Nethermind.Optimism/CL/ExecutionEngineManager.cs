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
    private ulong _currentFinalizedHead = 0;
    private ulong _currentSafeHead = 0;

    private Hash256 _currentHeadHash = Hash256.Zero;
    private Hash256 _currentFinalizedHash = Hash256.Zero;
    private Hash256 _currentSafeHash = Hash256.Zero;

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
        _currentFinalizedHead = finalizedBlock.Number;

        var safeBlock = _l2Api.GetSafeBlock();
        _currentSafeHash = safeBlock.Hash;
        _currentSafeHead = safeBlock.Number;

        if (_logger.IsInfo)
            _logger.Info($"EL manager initialization complete: current head {_currentHead}, current finalized head hash {_currentFinalizedHash}, current safe hash {_currentSafeHash}");
    }

    public async Task<bool> ProcessNewDerivedPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        // TODO: lock here
        if (_currentHead >= payloadAttributes.Number)
        {
            L2Block actualBlock = _l2Api.GetBlockByNumber(payloadAttributes.Number);
            if (_derivedBlocksVerifier.ComparePayloadAttributes(
                    actualBlock.PayloadAttributes, payloadAttributes.PayloadAttributes, payloadAttributes.Number))
            {
                return await SendForkChoiceUpdated(_currentHeadHash, actualBlock.Hash, actualBlock.Hash,
                    _currentHead, payloadAttributes.Number, payloadAttributes.Number);
            }

            return false;
        }

        ExecutionPayloadV3? executionPayload = await BuildBlockWithPayloadAttributes(payloadAttributes);
        if (executionPayload is null)
        {
            return false;
        }

        return await SendForkChoiceUpdated(_currentHeadHash, executionPayload.BlockHash, executionPayload.BlockHash,
            _currentHead, payloadAttributes.Number, payloadAttributes.Number);
    }

    public async Task<bool> ProcessNewP2PExecutionPayload(ExecutionPayloadV3 executionPayloadV3)
    {
        return await SendNewPayload(executionPayloadV3) &&
               await SendForkChoiceUpdated(executionPayloadV3.BlockHash, _currentFinalizedHash, _currentSafeHash,
                   (ulong)executionPayloadV3.BlockNumber, _currentFinalizedHead, _currentSafeHead);
    }

    private async Task<ExecutionPayloadV3?> BuildBlockWithPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        var fcuResult = await _l2Api.ForkChoiceUpdatedV3(
            _currentHeadHash, _currentFinalizedHash, _currentSafeHash,
            payloadAttributes.PayloadAttributes);

        if (fcuResult.PayloadStatus.Status != PayloadStatus.Valid)
        {
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

        if (npResult.Status == PayloadStatus.Invalid)
        {
            if (_logger.IsWarn) _logger.Warn($"Got invalid payload");
            return false;
        }
        return true;
    }

    private async Task<bool> SendForkChoiceUpdated(
        Hash256 headBlockHash, Hash256 finalizedBlockHash, Hash256 safeBlockHash,
        ulong head, ulong finalized, ulong safe)
    {
        ulong newFinalized  = _currentFinalizedHead;
        Hash256 newFinalizedHash = _currentFinalizedHash;
        if (_currentFinalizedHead < finalized)
        {
            newFinalized = finalized;
            newFinalizedHash = finalizedBlockHash;
        }

        ulong newSafe = _currentSafeHead;
        Hash256 newSafeHash = _currentSafeHash;
        if (_currentSafeHead < safe)
        {
            newSafe = safe;
            newSafeHash = safeBlockHash;
        }

        ulong newHead = _currentHead;
        Hash256 newHeadHash = _currentHeadHash;
        if (_currentHead < head)
        {
            newHead = head;
            newHeadHash = headBlockHash;
        }

        if (_currentHeadHash == newHeadHash && _currentFinalizedHash == newFinalizedHash &&
            _currentSafeHash == newSafeHash)
        {
            return true;
        }

        var result = await _l2Api.ForkChoiceUpdatedV3(newHeadHash, newFinalizedHash, newSafeHash);

        if (result.PayloadStatus.Status == PayloadStatus.Invalid)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid ForkChoiceUpdated({newHeadHash}, {newFinalizedHash}, {newSafeHash})");
            return false;
        }
        _currentHeadHash = newHeadHash;
        _currentFinalizedHash = newFinalizedHash;
        _currentSafeHash = newSafeHash;

        _currentSafeHead = newHead;
        _currentFinalizedHead = newFinalized;
        _currentSafeHead = newSafe;

        return true;
    }
}
