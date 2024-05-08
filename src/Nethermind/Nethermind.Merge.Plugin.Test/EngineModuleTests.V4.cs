// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [TestCase(
        "0x15d4d1f0b215b52458adeac09119798a7c35cebe4aba7f37f0014c7533e12b8a",
        "0xb457b256c8bbfc7f0716a0df076b4f72a358594d795be2aab001795fd297acdb",
        "0x58bb65def521baa8a64c1c9337d42da3885d7fbf85b8ad439cb179d480a64e98",
        "0xae9416e039eb988f")]
    public virtual async Task Should_process_block_as_expected_V4(string latestValidHash, string blockHash,
        string stateRoot, string payloadId)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Prague.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 prevRandao = Keccak.Zero;
        Address feeRecipient = TestItem.AddressC;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        var fcuState = new
        {
            headBlockHash = startingHead.ToString(),
            safeBlockHash = startingHead.ToString(),
            finalizedBlockHash = Keccak.Zero.ToString()
        };
        Withdrawal[] withdrawals = new[]
        {
            new Withdrawal { Index = 1, AmountInGwei = 3, Address = TestItem.AddressB, ValidatorIndex = 2 }
        };
        var payloadAttrs = new
        {
            timestamp = timestamp.ToHexString(true),
            prevRandao = prevRandao.ToString(),
            suggestedFeeRecipient = feeRecipient.ToString(),
            withdrawals,
            parentBeaconBLockRoot = Keccak.Zero
        };
        string?[] @params = new string?[]
        {
            chain.JsonSerializer.Serialize(fcuState), chain.JsonSerializer.Serialize(payloadAttrs)
        };
        string expectedPayloadId = payloadId;

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV3", @params!);
        JsonRpcSuccessResponse? successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        successResponse.Should().NotBeNull();
        response.Should().Be(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = new ForkchoiceUpdatedV1Result
            {
                PayloadId = expectedPayloadId,
                PayloadStatus = new PayloadStatusV1
                {
                    LatestValidHash = new(latestValidHash),
                    Status = PayloadStatus.Valid,
                    ValidationError = null
                }
            }
        }));

        Hash256 expectedBlockHash = new(blockHash);
        Block block = new(
            new(
                startingHead,
                Keccak.OfAnEmptySequenceRlp,
                feeRecipient,
                UInt256.Zero,
                1,
                chain.BlockTree.Head!.GasLimit,
                timestamp,
                Bytes.FromHexString("0x4e65746865726d696e64") // Nethermind
            )
            {
                BlobGasUsed = 0,
                ExcessBlobGas = 0,
                BaseFeePerGas = 0,
                Bloom = Bloom.Empty,
                GasUsed = 0,
                Hash = expectedBlockHash,
                MixHash = prevRandao,
                ParentBeaconBlockRoot = Keccak.Zero,
                ReceiptsRoot = chain.BlockTree.Head!.ReceiptsRoot!,
                StateRoot = new(stateRoot),
            },
            Array.Empty<Transaction>(),
            Array.Empty<BlockHeader>(),
            withdrawals);
        GetPayloadV4Result expectedPayload = new(block, UInt256.Zero, new BlobsBundleV1(block));

        response = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV4", expectedPayloadId);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        successResponse.Should().NotBeNull();
        response.Should().Be(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = expectedPayload
        }));

        response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV4",
            chain.JsonSerializer.Serialize(ExecutionPayloadV4.Create(block)), "[]", Keccak.Zero.ToString(true));
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        successResponse.Should().NotBeNull();
        response.Should().Be(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = new PayloadStatusV1
            {
                LatestValidHash = expectedBlockHash,
                Status = PayloadStatus.Valid,
                ValidationError = null
            }
        }));

        fcuState = new
        {
            headBlockHash = expectedBlockHash.ToString(true),
            safeBlockHash = expectedBlockHash.ToString(true),
            finalizedBlockHash = startingHead.ToString(true)
        };
        @params = new[] { chain.JsonSerializer.Serialize(fcuState), null };

        response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV3", @params!);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        successResponse.Should().NotBeNull();
        response.Should().Be(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = new ForkchoiceUpdatedV1Result
            {
                PayloadId = null,
                PayloadStatus = new PayloadStatusV1
                {
                    LatestValidHash = expectedBlockHash,
                    Status = PayloadStatus.Valid,
                    ValidationError = null
                }
            }
        }));
    }

    [TestCase(30)]
    public async Task can_progress_chain_one_by_one_v4(int count)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 lastHash = (await ProduceBranchV4(rpc, chain, count, CreateParentBlockRequestOnHead(chain.BlockTree), true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;
        chain.BlockTree.HeadHash.Should().Be(lastHash);
        Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, b => b.IsGenesis, true);
        last.Should().NotBeNull();
        last!.IsGenesis.Should().BeTrue();
    }

    [TestCase(30)]
    public async Task can_progress_chain_one_by_one_v4_with_requests(int count)
    {
        ConsensusRequestsProcessorMock consensusRequestsProcessorMock = new();
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance, null, null, null, consensusRequestsProcessorMock);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 lastHash = (await ProduceBranchV4(rpc, chain, count, CreateParentBlockRequestOnHead(chain.BlockTree), true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;
        chain.BlockTree.HeadHash.Should().Be(lastHash);
        Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, b => b.IsGenesis, true);
        last.Should().NotBeNull();
        last!.IsGenesis.Should().BeTrue();

        Block? head = chain.BlockTree.Head;
        head!.Requests!.Length.Should().Be(consensusRequestsProcessorMock.Requests.Length);
    }

    private async Task<IReadOnlyList<ExecutionPayload>> ProduceBranchV4(IEngineRpcModule rpc,
        MergeTestBlockchain chain,
        int count, ExecutionPayload startingParentBlock, bool setHead, Hash256? random = null)
    {
        List<ExecutionPayload> blocks = new();
        ExecutionPayload parentBlock = startingParentBlock;
        parentBlock.TryGetBlock(out Block? block);
        UInt256? startingTotalDifficulty = block!.IsGenesis
            ? block.Difficulty : chain.BlockFinder.FindHeader(block!.Header!.ParentHash!)!.TotalDifficulty;
        BlockHeader parentHeader = block!.Header;
        parentHeader.TotalDifficulty = startingTotalDifficulty +
                                       parentHeader.Difficulty;
        for (int i = 0; i < count; i++)
        {
            ExecutionPayloadV4? getPayloadResult = await BuildAndGetPayloadOnBranchV4(rpc, chain, parentHeader,
                parentBlock.Timestamp + 12,
                random ?? TestItem.KeccakA, Address.Zero);
            PayloadStatusV1 payloadStatusResponse = (await rpc.engine_newPayloadV4(getPayloadResult, Array.Empty<byte[]>(), Keccak.Zero)).Data;
            payloadStatusResponse.Status.Should().Be(PayloadStatus.Valid);
            if (setHead)
            {
                Hash256 newHead = getPayloadResult!.BlockHash;
                ForkchoiceStateV1 forkchoiceStateV1 = new(newHead, newHead, newHead);
                ResultWrapper<ForkchoiceUpdatedV1Result> setHeadResponse = await rpc.engine_forkchoiceUpdatedV3(forkchoiceStateV1);
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

    private async Task<ExecutionPayloadV4> BuildAndGetPayloadOnBranchV4(
        IEngineRpcModule rpc, MergeTestBlockchain chain, BlockHeader parentHeader,
        ulong timestamp, Hash256 random, Address feeRecipient)
    {
        PayloadAttributes payloadAttributes =
            new() { Timestamp = timestamp, PrevRandao = random, SuggestedFeeRecipient = feeRecipient, ParentBeaconBlockRoot = Keccak.Zero, Withdrawals = [] };

        // we're using payloadService directly, because we can't use fcU for branch
        string payloadId = chain.PayloadPreparationService!.StartPreparingPayload(parentHeader, payloadAttributes)!;

        ResultWrapper<GetPayloadV4Result?> getPayloadResult =
            await rpc.engine_getPayloadV4(Bytes.FromHexString(payloadId));
        return getPayloadResult.Data!.ExecutionPayload!;
    }
}
