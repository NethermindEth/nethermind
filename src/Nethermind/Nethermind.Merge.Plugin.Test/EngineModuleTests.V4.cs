// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        "0xb79ac644bd8fdacc11c408e2a143cd802baab81f13086fce79c471b38a8e0cd3",
        "0xa92cb7c874e510507d218c888a84b95a889fda2ef904803dcf42629ad3a27326",
        "0xa272b2f949e4a0e411c9b45542bd5d0ef3c311b5f26c4ed6b7a8d4f605a91154",
        "0xe7e7f64c04b395a6")]
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
            parentBeaconBLockRoot = Keccak.Zero,
            targetBlobCount = 0,
            maxBlobCount = 0,
        };
        string?[] @params = new string?[]
        {
            chain.JsonSerializer.Serialize(fcuState), chain.JsonSerializer.Serialize(payloadAttrs)
        };
        string expectedPayloadId = payloadId;

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV4", @params!);
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
                TargetBlobCount = 0,
            },
            Array.Empty<Transaction>(),
            Array.Empty<BlockHeader>(),
            withdrawals);
        GetPayloadV4Result expectedPayload = new(block, UInt256.Zero, new BlobsBundleV1(block), executionRequests: []);

        response = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV4", expectedPayloadId);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        successResponse.Should().NotBeNull();
        response.Should().Be(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = expectedPayload
        }));

        response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV4",
            chain.JsonSerializer.Serialize(ExecutionPayloadV3.Create(block)), "[]", Keccak.Zero.ToString(true), "[]");
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


    [Test]
    public async Task NewPayloadV4_reject_payload_with_bad_authorization_list_rlp()
    {
        ExecutionRequestsProcessorMock executionRequestsProcessorMock = new();
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance, null, null, null, executionRequestsProcessorMock);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 lastHash = (await ProduceBranchV4(rpc, chain, 10, CreateParentBlockRequestOnHead(chain.BlockTree), true, withRequests: true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;

        Transaction invalidSetCodeTx = Build.A.Transaction
          .WithType(TxType.SetCode)
          .WithNonce(0)
          .WithMaxFeePerGas(9.GWei())
          .WithMaxPriorityFeePerGas(9.GWei())
          .WithGasLimit(100_000)
          .WithAuthorizationCode(new JsonRpc.Test.Modules.Eth.EthRpcModuleTests.AllowNullAuthorizationTuple(0, null, 0, new Signature(new byte[65])))
          .WithTo(TestItem.AddressA)
          .SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        Block invalidBlock = Build.A.Block
            .WithNumber(chain.BlockTree.Head!.Number + 1)
            .WithTimestamp(chain.BlockTree.Head!.Timestamp + 12)
            .WithTransactions([invalidSetCodeTx])
            .WithParentBeaconBlockRoot(chain.BlockTree.Head!.ParentBeaconBlockRoot)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .TestObject;

        ExecutionPayloadV3 executionPayload = ExecutionPayloadV3.Create(invalidBlock);

        var response = await rpc.engine_newPayloadV4(executionPayload, [], invalidBlock.ParentBeaconBlockRoot, executionRequests: ExecutionRequestsProcessorMock.Requests);

        Assert.That(response.Data.Status, Is.EqualTo("INVALID"));
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
        ExecutionRequestsProcessorMock executionRequestsProcessorMock = new();
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance, null, null, null, executionRequestsProcessorMock);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 lastHash = (await ProduceBranchV4(rpc, chain, count, CreateParentBlockRequestOnHead(chain.BlockTree), true, withRequests: true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;
        chain.BlockTree.HeadHash.Should().Be(lastHash);
        Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, b => b.IsGenesis, true);
        last.Should().NotBeNull();
        last!.IsGenesis.Should().BeTrue();

        Block? head = chain.BlockTree.Head;
        head!.ExecutionRequests!.ToArray().Length.Should().Be(ExecutionRequestsProcessorMock.Requests.Length);
    }

    private async Task<IReadOnlyList<ExecutionPayload>> ProduceBranchV4(IEngineRpcModule rpc,
        MergeTestBlockchain chain,
        int count, ExecutionPayload startingParentBlock, bool setHead, Hash256? random = null, bool withRequests = false)
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
            ExecutionPayloadV3? getPayloadResult = await BuildAndGetPayloadOnBranchV4(rpc, chain, parentHeader,
                parentBlock.Timestamp + 12,
                random ?? TestItem.KeccakA, Address.Zero);
            PayloadStatusV1 payloadStatusResponse = (await rpc.engine_newPayloadV4(getPayloadResult, [], Keccak.Zero, executionRequests: withRequests ? ExecutionRequestsProcessorMock.Requests : new byte[][] { [], [], [] })).Data;
            payloadStatusResponse.Status.Should().Be(PayloadStatus.Valid);
            if (setHead)
            {
                Hash256 newHead = getPayloadResult!.BlockHash;
                ForkchoiceStateV1 forkchoiceStateV1 = new(newHead, newHead, newHead);
                ResultWrapper<ForkchoiceUpdatedV1Result> setHeadResponse = await rpc.engine_forkchoiceUpdatedV3(forkchoiceStateV1);
                setHeadResponse.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
                setHeadResponse.Data.PayloadId.Should().Be(null);
            }

            blocks.Add(getPayloadResult);
            parentBlock = getPayloadResult;
            parentBlock.TryGetBlock(out block!);
            block.Header.TotalDifficulty = parentHeader.TotalDifficulty + block.Header.Difficulty;
            parentHeader = block.Header;
        }

        return blocks;
    }

    private async Task<ExecutionPayloadV3> BuildAndGetPayloadOnBranchV4(
        IEngineRpcModule rpc, MergeTestBlockchain chain, BlockHeader parentHeader,
        ulong timestamp, Hash256 random, Address feeRecipient)
    {
        PayloadAttributes payloadAttributes =
            new()
            {
                Timestamp = timestamp,
                PrevRandao = random,
                SuggestedFeeRecipient = feeRecipient,
                ParentBeaconBlockRoot = Keccak.Zero,
                Withdrawals = [],
                TargetBlobCount = parentHeader.TargetBlobCount,
            };

        // we're using payloadService directly, because we can't use fcU for branch
        string payloadId = chain.PayloadPreparationService!.StartPreparingPayload(parentHeader, payloadAttributes)!;

        ResultWrapper<GetPayloadV4Result?> getPayloadResult =
            await rpc.engine_getPayloadV4(Bytes.FromHexString(payloadId));
        return getPayloadResult.Data!.ExecutionPayload!;
    }


    private static IEnumerable<IList<byte[]>> GetPayloadRequestsTestCases()
    {
        yield return ExecutionRequestsProcessorMock.Requests;
    }

    private async Task<ExecutionPayloadV3> BuildAndSendNewBlockV4(
        IEngineRpcModule rpc,
        MergeTestBlockchain chain,
        bool waitForBlockImprovement,
        Withdrawal[]? withdrawals)
    {
        Hash256 head = chain.BlockTree.HeadHash;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 random = Keccak.Zero;
        Address feeRecipient = Address.Zero;
        ExecutionPayloadV3 executionPayload = await BuildAndGetPayloadResultV4(rpc, chain, head,
            Keccak.Zero, head, timestamp, random, feeRecipient, withdrawals, waitForBlockImprovement);
        ResultWrapper<PayloadStatusV1> executePayloadResult =
            await rpc.engine_newPayloadV4(executionPayload, [], executionPayload.ParentBeaconBlockRoot, executionRequests: ExecutionRequestsProcessorMock.Requests);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);
        return executionPayload;
    }

    private async Task<ExecutionPayloadV3> BuildAndGetPayloadResultV4(
        IEngineRpcModule rpc,
        MergeTestBlockchain chain,
        Hash256 headBlockHash,
        Hash256 finalizedBlockHash,
        Hash256 safeBlockHash,
        ulong timestamp,
        Hash256 random,
        Address feeRecipient,
        Withdrawal[]? withdrawals,
        bool waitForBlockImprovement = true)
    {
        using SemaphoreSlim blockImprovementLock = new SemaphoreSlim(0);

        if (waitForBlockImprovement)
        {
            chain.PayloadPreparationService!.BlockImproved += (s, e) =>
            {
                blockImprovementLock.Release(1);
            };
        }

        ForkchoiceStateV1 forkchoiceState = new ForkchoiceStateV1(headBlockHash, finalizedBlockHash, safeBlockHash);
        PayloadAttributes payloadAttributes = new PayloadAttributes
        {
            Timestamp = timestamp,
            PrevRandao = random,
            SuggestedFeeRecipient = feeRecipient,
            ParentBeaconBlockRoot = Keccak.Zero,
            Withdrawals = withdrawals,
            TargetBlobCount = 0,
        };

        ResultWrapper<ForkchoiceUpdatedV1Result> result = rpc.engine_forkchoiceUpdatedV4(forkchoiceState, payloadAttributes).Result;
        string? payloadId = result.Data.PayloadId;

        if (waitForBlockImprovement)
            await blockImprovementLock.WaitAsync(10000);

        ResultWrapper<GetPayloadV4Result?> getPayloadResult =
            await rpc.engine_getPayloadV4(Bytes.FromHexString(payloadId!));

        return getPayloadResult.Data!.ExecutionPayload!;
    }
}
