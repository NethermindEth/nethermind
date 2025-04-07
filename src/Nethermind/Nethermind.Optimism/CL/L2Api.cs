// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public class L2Api(
    IOptimismEthRpcModule l2EthRpc,
    IOptimismEngineRpcModule l2EngineRpc,
    ISystemConfigDeriver systemConfigDeriver,
    ILogger logger) : IL2Api
{
    private const int L2ApiRetryDelayMilliseconds = 1000;

    public async Task<L2Block> GetBlockByNumber(ulong number)
    {
        var block = await RetryGetBlock(new((long)number));
        var payloadAttributes = PayloadAttributesFromBlockForRpc(block);
        return new L2Block
        {
            Hash = block.Hash,
            ParentHash = block.ParentHash,
            PayloadAttributesRef = payloadAttributes
        };
    }

    private PayloadAttributesRef PayloadAttributesFromBlockForRpc(BlockForRpc? block)
    {
        ArgumentNullException.ThrowIfNull(block);
        OptimismPayloadAttributes payloadAttributes = new()
        {
            NoTxPool = true,
            EIP1559Params = block.ExtraData.Length == 0 ? null : block.ExtraData[1..],
            GasLimit = block.GasLimit,
            ParentBeaconBlockRoot = block.ParentBeaconBlockRoot,
            PrevRandao = block.MixHash,
            SuggestedFeeRecipient = block.Miner,
            Timestamp = block.Timestamp.ToUInt64(null),
            Withdrawals = block.Withdrawals?.ToArray()
        };
        Transaction[] txs = block.Transactions.Cast<TransactionForRpc>().Select(t => t.ToTransaction()).ToArray();

        payloadAttributes.SetTransactions(txs);

        L1BlockInfo l1BlockInfo =
            L1BlockInfoBuilder.FromL2DepositTxDataAndExtraData(txs[0].Data!.Value.Span, block.ExtraData);
        SystemConfig systemConfig =
            systemConfigDeriver.SystemConfigFromL2BlockInfo(txs[0].Data!.Value.Span, block.ExtraData, (ulong)block.GasLimit);
        PayloadAttributesRef result = new()
        {
            PayloadAttributes = payloadAttributes,
            L1BlockInfo = l1BlockInfo,
            Number = (ulong)block.Number!,
            SystemConfig = systemConfig
        };
        return result;
    }

    public async Task<L2Block> GetHeadBlock()
    {
        var block = await RetryGetBlock(BlockParameter.Latest);
        var payloadAttributes = PayloadAttributesFromBlockForRpc(block);
        return new L2Block
        {
            Hash = block.Hash,
            ParentHash = block.ParentHash,
            PayloadAttributesRef = payloadAttributes
        };
    }

    public async Task<L2Block> GetFinalizedBlock()
    {
        var block = await RetryGetBlock(BlockParameter.Finalized);
        var payloadAttributes = PayloadAttributesFromBlockForRpc(block);
        return new L2Block
        {
            Hash = block.Hash,
            ParentHash = block.ParentHash,
            PayloadAttributesRef = payloadAttributes
        };
    }

    public async Task<L2Block> GetSafeBlock()
    {
        var block = await RetryGetBlock(BlockParameter.Safe);
        var payloadAttributes = PayloadAttributesFromBlockForRpc(block);
        return new L2Block
        {
            Hash = block.Hash,
            ParentHash = block.ParentHash,
            PayloadAttributesRef = payloadAttributes
        };
    }

    public async Task<ForkchoiceUpdatedV1Result> ForkChoiceUpdatedV3(Hash256 headHash, Hash256 finalizedHash, Hash256 safeHash,
        OptimismPayloadAttributes? payloadAttributes = null)
        => await RetryEngineApi(
            async () => await l2EngineRpc.engine_forkchoiceUpdatedV3(
                new ForkchoiceStateV1(headHash, finalizedHash, safeHash), payloadAttributes),
            err => $"ForkChoiceUpdated request error: {err}");

    public async Task<OptimismGetPayloadV3Result> GetPayloadV3(string payloadId)
    {
        byte[] payloadIdBytes = Bytes.FromHexString(payloadId);
        var getPayloadResult = await l2EngineRpc.engine_getPayloadV3(payloadIdBytes);
        while (getPayloadResult.Result.ResultType != ResultType.Success)
        {
            if (logger.IsWarn) logger.Warn($"GetPayload request error: {getPayloadResult.Result.Error}");
            await Task.Delay(L2ApiRetryDelayMilliseconds);
            getPayloadResult = await l2EngineRpc.engine_getPayloadV3(payloadIdBytes);
        }

        return getPayloadResult.Data!;
    }

    public async Task<PayloadStatusV1> NewPayloadV3(ExecutionPayloadV3 payload, Hash256? parentBeaconBlockRoot)
        => await RetryEngineApi(async () => await l2EngineRpc.engine_newPayloadV3(payload, [], parentBeaconBlockRoot),
            err => $"NewPayload request error: {err}");

    private async Task<BlockForRpc> RetryGetBlock(BlockParameter blockParameter)
    {
        var result = l2EthRpc.eth_getBlockByNumber(blockParameter, true);
        while (result?.Result.ResultType != ResultType.Success)
        {
            if (logger.IsWarn) logger.Warn($"Unable to get L2 block by parameter: {blockParameter}");
            await Task.Delay(L2ApiRetryDelayMilliseconds);
            result = l2EthRpc.eth_getBlockByNumber(blockParameter, true);
        }
        return result.Data;
    }

    private async Task<T> RetryEngineApi<T>(Func<Task<JsonRpc.ResultWrapper<T>>> rpcCall, Func<string?, string> getErrorMessage)
    {
        var result = await rpcCall();
        while (result?.Result.ResultType != ResultType.Success)
        {
            if (logger.IsWarn) logger.Warn(getErrorMessage(result!.Result.Error));
            await Task.Delay(L2ApiRetryDelayMilliseconds);
            result = await rpcCall();
        }
        return result.Data;
    }
}
