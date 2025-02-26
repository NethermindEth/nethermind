// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public class ExecutionEngineManager : IExecutionEngineManager
{
    private IOptimismEngineRpcModule _engineRpc;
    private IOptimismEthRpcModule _ethEngineRpc;
    private readonly ILogger _logger;

    private ulong _currentHead = 0;

    private Hash256? _currentHeadHash;
    private Hash256? _currentFinalizedHash;
    private Hash256? _currentSafeHash;

    public ExecutionEngineManager(IOptimismEngineRpcModule engineRpc, IOptimismEthRpcModule ethEngineRpc, ILogger logger)
    {
        _engineRpc = engineRpc;
        _ethEngineRpc = ethEngineRpc;
        _logger = logger;
    }

    public void Initialize()
    {
        var headBlockResult = _ethEngineRpc.eth_getBlockByNumber(BlockParameter.Latest);
        if (headBlockResult.Result != Result.Success)
        {
            throw new ArgumentException("Unable to get L2 execution engine head block");
        }
        BlockForRpc headBlock = headBlockResult.Data;
        _currentHead = (ulong)headBlock.Number!.Value;
        _currentHeadHash = headBlock.Hash;

        var finalizedBlockResult = _ethEngineRpc.eth_getBlockByNumber(BlockParameter.Finalized);
        if (finalizedBlockResult.Result != Result.Success)
        {
            throw new ArgumentException("Unable to get L2 execution engine finalized block");
        }
        BlockForRpc finalizedBlock = finalizedBlockResult.Data;
        _currentFinalizedHash = finalizedBlock.Hash;
        _currentSafeHash = _currentFinalizedHash;
        _logger.Error($"EL manager initialization complete: current head {_currentHead}, current finalized head hash {_currentFinalizedHash}");
        // TODO: fix safe head
    }

    public async Task ProcessNewDerivedPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        if (_currentHead >= payloadAttributes.Number)
        {
            VerifyOldPayloadAttributes(payloadAttributes);
        }
        else
        {
            await BuildBlockWithPayloadAttributes(payloadAttributes);
        }
    }

    public Task ProcessNewP2PExecutionPayload(ExecutionPayloadV3 executionPayloadV3)
    {
        throw new System.NotImplementedException();
    }

    private async Task BuildBlockWithPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        _logger.Error($"Sending fcu with pas");
        var fcuResult = await _engineRpc.engine_forkchoiceUpdatedV3(
            new ForkchoiceStateV1(_currentHeadHash!, _currentFinalizedHash!, _currentSafeHash!),
            payloadAttributes.PayloadAttributes);
        if (fcuResult.Result != Result.Success)
        {
            throw new ArgumentException($"Unable to send fcu with payload attributes to EL. Error: {fcuResult.Result.Error}");
        }

        if (fcuResult.Data.PayloadStatus.Status != PayloadStatus.Valid)
        {
            throw new ArgumentException($"Invalid payload status {fcuResult.Data.PayloadStatus.Status}");
        }

        await Task.Delay(100); // TODO: for how long should we wait?

        _logger.Error($"Sending getPayload");
        byte[] payloadId = Convert.FromHexString(fcuResult.Data.PayloadId!.Substring(2));
        var getPayloadResult = await _engineRpc.engine_getPayloadV3(payloadId);

        if (getPayloadResult.Result != Result.Success)
        {
            throw new ArgumentException($"Unable to build block. Error: {getPayloadResult.Result.Error}");
        }

        ExecutionPayloadV3 executionPayload = (ExecutionPayloadV3)getPayloadResult.Data!.ExecutionPayload;
        _currentHead = (ulong)executionPayload.BlockNumber;
        _currentHeadHash = executionPayload.BlockHash;

        _logger.Error($"Sending newPayload");
        var newPayloadV3Result = await _engineRpc.engine_newPayloadV3(executionPayload, [], payloadAttributes.PayloadAttributes.ParentBeaconBlockRoot);

        if (newPayloadV3Result.Result != Result.Success)
        {
            throw new ArgumentException($"Unexpected np result. Error: {newPayloadV3Result.Result.Error}");
        }

        if (newPayloadV3Result.Data.Status != PayloadStatus.Valid)
        {
            throw new ArgumentException($"Invalid payload status {newPayloadV3Result.Data.Status}");
        }

        _logger.Error($"Sending final fcu");
        fcuResult = await _engineRpc.engine_forkchoiceUpdatedV3(
            new ForkchoiceStateV1(_currentHeadHash!, _currentFinalizedHash!, _currentSafeHash!),
            null);
        if (fcuResult.Result != Result.Success)
        {
            throw new ArgumentException($"Unable to send fcu to EL. Error: {fcuResult.Result.Error}");
        }
    }

    private void VerifyOldPayloadAttributes(PayloadAttributesRef payloadAttributes)
    {
        _logger.Error($"EL manager verify old payload attributes {payloadAttributes.Number}");
        // TODO: check that payloadAttributes match ether p2p block that we already sent to EL. Or ask EL for block through rpc
    }
}
