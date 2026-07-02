// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public void NewPayloadV4_parameters_have_cached_deserialization_metadata()
    {
        JsonRpcConfig jsonRpcConfig = new() { EnabledModules = [ModuleType.Engine] };
        RpcModuleProvider moduleProvider = new(new RealFileSystem(), jsonRpcConfig, new EthereumJsonSerializer(), LimboLogs.Instance);
        moduleProvider.Register(new SingletonModulePool<IEngineRpcModule>(Substitute.For<IEngineRpcModule>(), true));

        RpcModuleProvider.ResolvedMethodInfo method = moduleProvider.Resolve(nameof(IEngineRpcModule.engine_newPayloadV4))!;
        RpcModuleProvider.ResolvedMethodInfo.ExpectedParameter[] parameters = method.ExpectedParameters;

        Assert.That(parameters.Length, Is.EqualTo(4));
        AssertParameter(parameters[0], "executionPayload", typeof(ExecutionPayloadV3));
        AssertParameter(parameters[1], "blobVersionedHashes", typeof(Hash256[]));
        AssertParameter(parameters[2], "parentBeaconBlockRoot", typeof(Hash256));
        AssertParameter(parameters[3], "executionRequests", typeof(byte[][]));

        static void AssertParameter(
            RpcModuleProvider.ResolvedMethodInfo.ExpectedParameter parameter,
            string name,
            Type type)
        {
            Assert.That(parameter.Info.Name, Is.EqualTo(name));
            Assert.That(parameter.ParameterType, Is.EqualTo(type));
            Assert.That(parameter.Kind, Is.EqualTo(RpcModuleProvider.ResolvedMethodInfo.ParameterKind.Typed));
            Assert.That(parameter.TypeInfo, Is.Not.Null);
            Assert.That(parameter.TypeInfo!.Type, Is.EqualTo(type));
        }
    }

    [TestCase(
        "0x32fc756d56a1897bf4d53ec72a854743786b5edbd8c0feec05b245e7cc124eea",
        "0xc1ecac4884c36982061392807b9307fb0d701a05d9d9fd7dc7e82d2ec96cf9af",
        "0x8ffb712de6b72f59def7b84f361e6c23519f7f8674d7e6552e23617a996d8ed3",
        "0x0f0b18188ed90425")]
    [NonParallelizable]
    public virtual async Task Should_process_block_as_expected_V4(string latestValidHash, string blockHash,
        string stateRoot, string payloadId)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Prague.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash!;
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

        Assert.That(successResponse, Is.Not.Null);
        Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
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
        })));

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

        Assert.That(successResponse, Is.Not.Null);
        Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = expectedPayload
        })));

        response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV4",
            chain.JsonSerializer.Serialize(ExecutionPayloadV3.Create(block)), "[]", Keccak.Zero.ToString(true), "[]");
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        Assert.That(successResponse, Is.Not.Null);
        Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = new PayloadStatusV1
            {
                LatestValidHash = expectedBlockHash,
                Status = PayloadStatus.Valid,
                ValidationError = null
            }
        })));

        fcuState = new
        {
            headBlockHash = expectedBlockHash.ToString(true),
            safeBlockHash = expectedBlockHash.ToString(true),
            finalizedBlockHash = startingHead.ToString(true)
        };
        @params = new[] { chain.JsonSerializer.Serialize(fcuState), null };

        response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV3", @params!);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        Assert.That(successResponse, Is.Not.Null);
        Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
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
        })));
    }


    [Test]
    public async Task NewPayloadV4_reject_payload_with_bad_authorization_list_rlp()
    {
        ExecutionRequestsProcessorMock executionRequestsProcessorMock = new();
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance, null, null, executionRequestsProcessorMock);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 lastHash = (await ProduceBranchV4(rpc, chain, 10, CreateParentBlockRequestOnHead(chain.BlockTree), true, withRequests: true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;

        Transaction invalidSetCodeTx = Build.A.Transaction
          .WithType(TxType.SetCode)
          .WithNonce(0)
          .WithMaxFeePerGas(9.GWei)
          .WithMaxPriorityFeePerGas(9.GWei)
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

        ResultWrapper<PayloadStatusV1> response = await rpc.engine_newPayloadV4(executionPayload, [], invalidBlock.ParentBeaconBlockRoot, executionRequests: ExecutionRequestsProcessorMock.Requests);

        Assert.That(response.Data.Status, Is.EqualTo("INVALID"));
    }

    [Test]
    public async Task NewPayloadV4_reject_payload_with_bad_execution_requests()
    {
        ExecutionRequestsProcessorMock executionRequestsProcessorMock = new();
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance, null, null, executionRequestsProcessorMock);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 lastHash = (await ProduceBranchV4(rpc, chain, 10, CreateParentBlockRequestOnHead(chain.BlockTree), true, withRequests: true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;

        Block TestBlock = Build.A.Block.WithNumber(chain.BlockTree.Head!.Number + 1).TestObject;
        ExecutionPayloadV3 executionPayload = ExecutionPayloadV3.Create(TestBlock);

        // must reject if execution requests types are not in ascending order
        ResultWrapper<PayloadStatusV1> response = await rpc.engine_newPayloadV4(
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

    [Test]
    public async Task NewPayloadV4_returns_invalid_params_for_block_access_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance);
        Block block = Build.A.Block
            .WithNumber(chain.BlockTree.Head!.Number + 1)
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .TestObject;
        ExecutionPayloadV3 executionPayload = ExecutionPayloadV3.Create(block);
        executionPayload.BlockAccessList = Bytes.FromHexString("0xc0");

        ResultWrapper<PayloadStatusV1> response = await chain.EngineRpcModule.engine_newPayloadV4(
            executionPayload,
            [],
            Keccak.Zero,
            []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
        }
    }

    [TestCase(30)]
    public async Task can_progress_chain_one_by_one_v4(int count)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 lastHash = (await ProduceBranchV4(rpc, chain, count, CreateParentBlockRequestOnHead(chain.BlockTree), true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;
        Assert.That(chain.BlockTree.HeadHash, Is.EqualTo(lastHash));
        Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, static b => b.IsGenesis, true);
        Assert.That(last, Is.Not.Null);
        Assert.That(last!.IsGenesis, Is.True);
    }

    [TestCase(30)]
    public async Task can_progress_chain_one_by_one_v4_with_requests(int count)
    {
        ExecutionRequestsProcessorMock executionRequestsProcessorMock = new();
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance, null, null, executionRequestsProcessorMock);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 lastHash = (await ProduceBranchV4(rpc, chain, count, CreateParentBlockRequestOnHead(chain.BlockTree), true, withRequests: true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;
        Assert.That(chain.BlockTree.HeadHash, Is.EqualTo(lastHash));
        Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, static b => b.IsGenesis, true);
        Assert.That(last, Is.Not.Null);
        Assert.That(last!.IsGenesis, Is.True);

        Block? head = chain.BlockTree.Head;
        // ExecutionRequests is a transient property (not in RLP), so it may not survive
        // cache round-trips. Verify via RequestsHash on the header instead, which IS persisted.
        Assert.That(head!.Header.RequestsHash, Is.EqualTo(ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(ExecutionRequestsProcessorMock.Requests)));
    }

    private async Task<IReadOnlyList<ExecutionPayload>> ProduceBranchV4(IEngineRpcModule rpc,
        MergeTestBlockchain chain,
        int count, ExecutionPayload startingParentBlock, bool setHead, Hash256? random = null, bool withRequests = false)
    {
        List<ExecutionPayload> blocks = [];
        ExecutionPayload parentBlock = startingParentBlock;
        Block? block = parentBlock.TryGetBlock().Data;
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
            PayloadStatusV1 payloadStatusResponse = (await rpc.engine_newPayloadV4(getPayloadResult, [], Keccak.Zero, executionRequests: withRequests ? ExecutionRequestsProcessorMock.Requests : Array.Empty<byte[]>())).Data;
            Assert.That(payloadStatusResponse.Status, Is.EqualTo(PayloadStatus.Valid));
            if (setHead)
            {
                Hash256 newHead = getPayloadResult!.BlockHash!;
                ForkchoiceStateV1 forkchoiceStateV1 = new(newHead, newHead, newHead);
                ResultWrapper<ForkchoiceUpdatedV1Result> setHeadResponse = await rpc.engine_forkchoiceUpdatedV3(forkchoiceStateV1);
                Assert.That(setHeadResponse.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
                Assert.That(setHeadResponse.Data.PayloadId, Is.EqualTo(null));
            }

            blocks.Add(getPayloadResult);
            parentBlock = getPayloadResult;
            block = parentBlock.TryGetBlock().Data!;
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
        Hash256 head = chain.BlockTree.HeadHash!;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 random = Keccak.Zero;
        Address feeRecipient = Address.Zero;
        ExecutionPayloadV3 executionPayload = await BuildAndGetPayloadResultV4(rpc, chain, head,
            Keccak.Zero, head, timestamp, random, feeRecipient, withdrawals, waitForBlockImprovement);
        ResultWrapper<PayloadStatusV1> executePayloadResult =
            await rpc.engine_newPayloadV4(executionPayload, [], executionPayload.ParentBeaconBlockRoot, executionRequests: ExecutionRequestsProcessorMock.Requests);
        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));
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

        ForkchoiceStateV1 forkchoiceState = new(headBlockHash, finalizedBlockHash, safeBlockHash);
        PayloadAttributes payloadAttributes = new()
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
