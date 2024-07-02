// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Abstractions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Taiko;

public class TaikoEngineRpcModule : ITaikoEngineRpcModule
{
    private readonly IEngineRpcModule _engineRpcModule;

    public ResultWrapper<TransitionConfigurationV1> engine_exchangeTransitionConfigurationV1(TransitionConfigurationV1 beaconTransitionConfiguration)
    {
        return _engineRpcModule.engine_exchangeTransitionConfigurationV1(beaconTransitionConfiguration);
    }

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null)
    {
        return await _engineRpcModule.engine_forkchoiceUpdatedV1(forkchoiceState, payloadAttributes);
    }

    public Task<ResultWrapper<ExecutionPayload?>> engine_getPayloadV1(byte[] payloadId)
    {
        return _engineRpcModule.engine_getPayloadV1(payloadId);
    }

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(TaikoExecutionPayload executionPayload)
    {
        return _engineRpcModule.engine_newPayloadV1(executionPayload);
    }

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null)
    {
        return await _engineRpcModule.engine_forkchoiceUpdatedV2(forkchoiceState, payloadAttributes);
    }

    public Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId)
    {
        return _engineRpcModule.engine_getPayloadV2(payloadId);
    }

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(TaikoExecutionPayload executionPayload)
    {
        return _engineRpcModule.engine_newPayloadV2(executionPayload);
    }

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV3(ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null)
    {
        return await _engineRpcModule.engine_forkchoiceUpdatedV3(forkchoiceState, payloadAttributes);
    }

    public Task<ResultWrapper<GetPayloadV3Result?>> engine_getPayloadV3(byte[] payloadId)
    {
        return _engineRpcModule.engine_getPayloadV3(payloadId);
    }

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(TaikoExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot)
    {
        return _engineRpcModule.engine_newPayloadV3(executionPayload, blobVersionedHashes, parentBeaconBlockRoot);
    }

    public TaikoEngineRpcModule(IEngineRpcModule engineRpcModule)
    {
        _engineRpcModule = engineRpcModule;
    }
}

public class TaikoExecutionPayloadV3 : ExecutionPayloadV3
{
    public Hash256 WithdrawalsHash { get; set; } = Keccak.Zero;
    public Hash256 TransactionsHash { get; set; } = Keccak.Zero;

    public override ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error)
    {
        if (spec.IsEip4844Enabled)
        {
            error = "ExecutionPayloadV3 expected";
            return ValidationResult.Fail;
        }

        int actualVersion = this switch
        {
            { BlobGasUsed: not null } or { ExcessBlobGas: not null } or { ParentBeaconBlockRoot: not null } => 3,
            { WithdrawalsHash: not null } or { Withdrawals: not null } => 2, // modified
            _ => 1
        };

        error = actualVersion switch
        {
            1 when spec.WithdrawalsEnabled => "ExecutionPayloadV2 expected",
            > 1 when !spec.WithdrawalsEnabled => "ExecutionPayloadV1 expected",
            _ => actualVersion > version ? $"ExecutionPayloadV{version} expected" : null
        };

        return error is null ? ValidationResult.Success : ValidationResult.Fail;
    }
}

public class TaikoExecutionPayload : ExecutionPayload
{
    public Hash256? WithdrawalsHash { get; set; } = null;
    public Hash256? TxHash { get; set; } = null;

    public override ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error)
    {
        if (spec.IsEip4844Enabled)
        {
            error = "ExecutionPayloadV3 expected";
            return ValidationResult.Fail;
        }

        int actualVersion = this switch
        {
            { BlobGasUsed: not null } or { ExcessBlobGas: not null } or { ParentBeaconBlockRoot: not null } => 3,
            { WithdrawalsHash: not null } or { Withdrawals: not null } => 2, // modified
            _ => 1
        };

        error = actualVersion switch
        {
            1 when spec.WithdrawalsEnabled => "ExecutionPayloadV2 expected",
            > 1 when !spec.WithdrawalsEnabled => "ExecutionPayloadV1 expected",
            _ => actualVersion > version ? $"ExecutionPayloadV{version} expected" : null
        };

        return error is null ? ValidationResult.Success : ValidationResult.Fail;
    }

    public override bool TryGetBlock(out Block? block, UInt256? totalDifficulty = null)
    {
        if (Withdrawals is null && Transactions is null)
        {
            var header = new BlockHeader(
                ParentHash,
                Keccak.OfAnEmptySequenceRlp,
                FeeRecipient,
                UInt256.Zero,
                BlockNumber,
                GasLimit,
                Timestamp,
                ExtraData)
            {
                Hash = BlockHash,
                ReceiptsRoot = ReceiptsRoot,
                StateRoot = StateRoot,
                Bloom = LogsBloom,
                GasUsed = GasUsed,
                BaseFeePerGas = BaseFeePerGas,
                Nonce = 0,
                MixHash = PrevRandao,
                Author = FeeRecipient,
                IsPostMerge = true,
                TotalDifficulty = totalDifficulty,
                TxRoot = TxHash,
                WithdrawalsRoot = WithdrawalsHash,
            };

            block = new(header, Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), null);
            return true;
        }
        return base.TryGetBlock(out block, totalDifficulty);
    }
}
