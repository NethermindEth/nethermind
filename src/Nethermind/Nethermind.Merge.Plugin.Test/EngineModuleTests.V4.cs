// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
        "0x9e205909311e6808bd7167e07bda30bda2b1061127e89e76167781214f3024bf",
        "0x701f48fd56e6ded89a9ec83926eb99eebf9a38b15b4b8f0066574ac1dd9ff6df",
        "0x73cecfc66bc1c8545aa3521e21be51c31bd2054badeeaa781f5fd5b871883f35",
        "0x80ce7f68a5211b5d")]
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
        GetPayloadV4Result expectedPayload = new(block, UInt256.Zero, new BlobsBundleV1(block), executionRequests: [], shouldOverrideBuilder: false);

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

    [Test]
    public async Task NewPayloadV4_reject_payload_with_bad_execution_requests()
    {
        ExecutionRequestsProcessorMock executionRequestsProcessorMock = new();
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance, null, null, null, executionRequestsProcessorMock);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 lastHash = (await ProduceBranchV4(rpc, chain, 10, CreateParentBlockRequestOnHead(chain.BlockTree), true, withRequests: true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;

        Block TestBlock = Build.A.Block.WithNumber(chain.BlockTree.Head!.Number + 1).TestObject;
        ExecutionPayloadV3 executionPayload = ExecutionPayloadV3.Create(TestBlock);

        // must reject if execution requests types are not in ascending order
        var response = await rpc.engine_newPayloadV4(
                executionPayload,
                [],
                TestBlock.ParentBeaconBlockRoot,
                executionRequests: [Bytes.FromHexString("0x0001"), Bytes.FromHexString("0x0101"), Bytes.FromHexString("0x0101")]
        );

        Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));

        //must reject if one of the execution requests size is <= 1 byte
        response = await rpc.engine_newPayloadV4(
                executionPayload,
                [],
                TestBlock.ParentBeaconBlockRoot,
                executionRequests: [Bytes.FromHexString("0x0001"), Bytes.FromHexString("0x01"), Bytes.FromHexString("0x0101")]
        );

        Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [TestCase(30)]
    public async Task can_progress_chain_one_by_one_v4(int count)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 lastHash = (await ProduceBranchV4(rpc, chain, count, CreateParentBlockRequestOnHead(chain.BlockTree), true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;
        chain.BlockTree.HeadHash.Should().Be(lastHash);
        Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, static b => b.IsGenesis, true);
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
        Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, static b => b.IsGenesis, true);
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
        Block? block = parentBlock.TryGetBlock().Block;
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
            PayloadStatusV1 payloadStatusResponse = (await rpc.engine_newPayloadV4(getPayloadResult, [], Keccak.Zero, executionRequests: withRequests ? ExecutionRequestsProcessorMock.Requests : new byte[][] { })).Data;
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
            block = parentBlock.TryGetBlock().Block!;
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
            new() { Timestamp = timestamp, PrevRandao = random, SuggestedFeeRecipient = feeRecipient, ParentBeaconBlockRoot = Keccak.Zero, Withdrawals = [] };

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
        Task blockImprovementWait = waitForBlockImprovement
            ? chain.WaitForImprovedBlock()
            : Task.CompletedTask;

        ForkchoiceStateV1 forkchoiceState = new ForkchoiceStateV1(headBlockHash, finalizedBlockHash, safeBlockHash);
        PayloadAttributes payloadAttributes = new PayloadAttributes
        {
            Timestamp = timestamp,
            PrevRandao = random,
            SuggestedFeeRecipient = feeRecipient,
            ParentBeaconBlockRoot = Keccak.Zero,
            Withdrawals = withdrawals,
        };

        ResultWrapper<ForkchoiceUpdatedV1Result> result = rpc.engine_forkchoiceUpdatedV3(forkchoiceState, payloadAttributes).Result;
        string? payloadId = result.Data.PayloadId;

        await blockImprovementWait;

        ResultWrapper<GetPayloadV4Result?> getPayloadResult =
            await rpc.engine_getPayloadV4(Bytes.FromHexString(payloadId!));

        return getPayloadResult.Data!.ExecutionPayload!;
    }
}
