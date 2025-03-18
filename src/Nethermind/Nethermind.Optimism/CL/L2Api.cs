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
    public L2Block GetBlockByNumber(ulong number)
    {
        var blockResult = l2EthRpc.eth_getBlockByNumber(new((long)number), true);
        if (blockResult.Result != Result.Success)
        {
            logger.Error($"Unable to get L2 block by number: {number}");
            throw new Exception($"Unable to get L2 block by number: {blockResult.Result}");
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

    public L2Block GetHeadBlock()
    {
        var blockResult = l2EthRpc.eth_getBlockByNumber(BlockParameter.Latest, true);
        if (blockResult.Result != Result.Success)
        {
            logger.Error($"Unable to get L2 head block");
            throw new Exception($"Unable to get L2 head block");
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

    public L2Block GetFinalizedBlock()
    {
        var blockResult = l2EthRpc.eth_getBlockByNumber(BlockParameter.Finalized, true);
        if (blockResult.Result != Result.Success)
        {
            logger.Error($"Unable to get L2 finalized block");
            throw new Exception($"Unable to get L2 finalized block");
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

    public L2Block GetSafeBlock()
    {
        var blockResult = l2EthRpc.eth_getBlockByNumber(BlockParameter.Safe, true);
        if (blockResult.Result != Result.Success)
        {
            logger.Error($"Unable to get L2 safe block");
            throw new Exception($"Unable to get L2 safe block");
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
        var result = await l2EngineRpc.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(headHash, finalizedHash, safeHash), payloadAttributes);

        if (result.Result.ResultType == ResultType.Failure)
        {
            if (logger.IsError) logger.Error($"ForkChoiceUpdated request error: {result.Result.Error}");
            throw new Exception($"ForkChoiceUpdated request error: {result.Result.Error}");
        }
        return result.Data;
    }

    public async Task<OptimismGetPayloadV3Result> GetPayloadV3(string payloadId)
    {
        byte[] payloadIdBytes = Convert.FromHexString(payloadId.Substring(2));
        var getPayloadResult = await l2EngineRpc.engine_getPayloadV3(payloadIdBytes);
        if (getPayloadResult.Result != Result.Success)
        {
            throw new ArgumentException($"Unable to build block. Error: {getPayloadResult.Result.Error}");
        }

        return getPayloadResult.Data!;
    }

    public async Task<PayloadStatusV1> NewPayloadV3(ExecutionPayloadV3 payload, Hash256? parentBeaconBlockRoot)
    {
        var npResult = await l2EngineRpc.engine_newPayloadV3(payload, [], parentBeaconBlockRoot);

        if (npResult.Result.ResultType == ResultType.Failure)
        {
            if (logger.IsError) logger.Error($"NewPayload request error: {npResult.Result.Error}");
            throw new Exception($"NewPayload request error: {npResult.Result.Error}");
        }
        return npResult.Data;
    }
}
