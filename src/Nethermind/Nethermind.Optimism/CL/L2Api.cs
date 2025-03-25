// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public class L2Api(IOptimismEthRpcModule l2EthRpc, IOptimismEngineRpcModule l2EngineRpc, ISystemConfigDeriver systemConfigDeriver, ILogger logger) : IL2Api
{
    private const int L2ApiRetryDelayMilliseconds = 1000;

    public async Task<L2Block> GetBlockByNumber(ulong number)
    {
        var blockResult = l2EthRpc.eth_getBlockByNumber(new((long)number), true);
        while (blockResult.Result.ResultType != ResultType.Success)
        {
            if (logger.IsWarn) logger.Warn($"Unable to get L2 block by number: {number}");
            await Task.Delay(L2ApiRetryDelayMilliseconds);
            blockResult = l2EthRpc.eth_getBlockByNumber(new((long)number), true);
        }
        var block = blockResult.Data;
        var payloadAttributes = PayloadAttributesFromBlockForRpc(block);
        return new L2Block
        {
            Hash = block.Hash,
            ParentHash = block.ParentHash,
            PayloadAttributesRef = payloadAttributes
        };
    }

    private PayloadAttributesRef PayloadAttributesFromBlockForRpc(BlockForRpc block)
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
        Transaction[] txs = block.Transactions.Cast<TransactionForRpc>().Select(t =>
        {
            Transaction nativeTx = t.ToTransaction();
            return nativeTx;
        }).ToArray();

        payloadAttributes.SetTransactions(txs);

        L1BlockInfo l1BlockInfo =
            L1BlockInfoBuilder.FromL2DepositTxDataAndExtraData(txs[0].Data!.Value.ToArray(), block.ExtraData);
        SystemConfig systemConfig =
            systemConfigDeriver.SystemConfigFromL2BlockInfo(txs[0].Data!.Value.ToArray(), block.ExtraData, (ulong)block.GasLimit);
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
        var blockResult = l2EthRpc.eth_getBlockByNumber(BlockParameter.Latest, true);
        while (blockResult.Result.ResultType != ResultType.Success)
        {
            if (logger.IsWarn) logger.Warn($"Unable to get L2 head block");
            await Task.Delay(L2ApiRetryDelayMilliseconds);
            blockResult = l2EthRpc.eth_getBlockByNumber(BlockParameter.Latest, true);
        }
        var block = blockResult.Data;
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
        var blockResult = l2EthRpc.eth_getBlockByNumber(BlockParameter.Finalized, true);
        while (blockResult.Result.ResultType != ResultType.Success)
        {
            if (logger.IsWarn) logger.Warn("Unable to get L2 finalized block");
            await Task.Delay(L2ApiRetryDelayMilliseconds);
            blockResult = l2EthRpc.eth_getBlockByNumber(BlockParameter.Finalized, true);
        }
        var block = blockResult.Data;
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
        var blockResult = l2EthRpc.eth_getBlockByNumber(BlockParameter.Safe, true);
        while (blockResult.Result.ResultType != ResultType.Success)
        {
            if (logger.IsWarn) logger.Warn($"Unable to get L2 safe block");
            await Task.Delay(L2ApiRetryDelayMilliseconds);
            blockResult = l2EthRpc.eth_getBlockByNumber(BlockParameter.Safe, true);
        }
        var block = blockResult.Data;
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
    {
        var fcuResult = await l2EngineRpc.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(headHash, finalizedHash, safeHash), payloadAttributes);

        while (fcuResult.Result.ResultType != ResultType.Success)
        {
            if (logger.IsWarn) logger.Warn($"ForkChoiceUpdated request error: {fcuResult.Result.Error}");
            await Task.Delay(L2ApiRetryDelayMilliseconds);
            fcuResult = await l2EngineRpc.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(headHash, finalizedHash, safeHash), payloadAttributes);
        }
        return fcuResult.Data;
    }

    public async Task<OptimismGetPayloadV3Result> GetPayloadV3(string payloadId)
    {
        byte[] payloadIdBytes = Convert.FromHexString(payloadId.Substring(2));
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
    {
        var npResult = await l2EngineRpc.engine_newPayloadV3(payload, [], parentBeaconBlockRoot);
        while (npResult.Result.ResultType != ResultType.Success)
        {
            if (logger.IsWarn) logger.Warn($"NewPayload request error: {npResult.Result.Error}");
            await Task.Delay(L2ApiRetryDelayMilliseconds);
            npResult = await l2EngineRpc.engine_newPayloadV3(payload, [], parentBeaconBlockRoot);
        }
        return npResult.Data;
    }
}
