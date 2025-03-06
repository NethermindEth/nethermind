// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Test;

public class EngineModuleTestHelpers(
    IEngineRpcModule rpc,
    IBlockTree blockTree,
    IBlockFinder blockFinder,
    IPayloadPreparationService payloadPreparationService
) {
    public async Task<IReadOnlyList<ExecutionPayload>> ProduceBranchV1(int count, ExecutionPayload startingParentBlock, bool setHead, Hash256? random = null,
        ulong slotLength = 12)
    {
        List<ExecutionPayload> blocks = new();
        ExecutionPayload parentBlock = startingParentBlock;
        parentBlock.TryGetBlock(out Block? block);
        UInt256? startingTotalDifficulty = block!.IsGenesis
            ? block.Difficulty : blockFinder.FindHeader(block!.Header!.ParentHash!)!.TotalDifficulty;
        BlockHeader parentHeader = block!.Header;
        parentHeader.TotalDifficulty = startingTotalDifficulty +
                                       parentHeader.Difficulty;
        for (int i = 0; i < count; i++)
        {
            ExecutionPayload? getPayloadResult = await BuildAndGetPayloadOnBranch(parentHeader,
                parentBlock.Timestamp + slotLength,
                random ?? TestItem.KeccakA, Address.Zero);
            PayloadStatusV1 payloadStatusResponse = (await rpc.engine_newPayloadV1(getPayloadResult)).Data;
            payloadStatusResponse.Status.Should().Be(PayloadStatus.Valid);
            if (setHead)
            {
                Hash256 newHead = getPayloadResult!.BlockHash;
                ForkchoiceStateV1 forkchoiceStateV1 = new(newHead, newHead, newHead);
                ResultWrapper<ForkchoiceUpdatedV1Result> setHeadResponse = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
                setHeadResponse.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
                setHeadResponse.Data.PayloadId.Should().Be(null);
            }

            blocks.Add((getPayloadResult));
            parentBlock = getPayloadResult;
            parentBlock.TryGetBlock(out block!);
            block.Header.TotalDifficulty = parentHeader.TotalDifficulty + block.Header.Difficulty;
            parentHeader = block.Header;
        }

        return blocks;
    }

    protected async Task<ExecutionPayload> BuildAndGetPayloadOnBranch(
        BlockHeader parentHeader,
        ulong timestamp, Hash256 random, Address feeRecipient)
    {
        PayloadAttributes payloadAttributes =
            new() { Timestamp = timestamp, PrevRandao = random, SuggestedFeeRecipient = feeRecipient };

        // we're using payloadService directly, because we can't use fcU for branch
        string payloadId = payloadPreparationService!.StartPreparingPayload(parentHeader, payloadAttributes)!;

        ResultWrapper<ExecutionPayload?> getPayloadResult =
            await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));
        return getPayloadResult.Data!;
    }

    public ExecutionPayload CreateParentBlockRequestOnHead()
    {
        Block? head = blockTree.Head ?? throw new NotSupportedException();
        return new ExecutionPayload()
        {
            BlockNumber = head.Number,
            BlockHash = head.Hash!,
            StateRoot = head.StateRoot!,
            ReceiptsRoot = head.ReceiptsRoot!,
            GasLimit = head.GasLimit,
            Timestamp = head.Timestamp,
            BaseFeePerGas = head.BaseFeePerGas,
        };
    }
}
