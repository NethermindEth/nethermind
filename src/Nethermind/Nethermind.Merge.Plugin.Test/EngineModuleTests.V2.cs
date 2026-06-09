// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [TestCase(
        "0x73b538422b679dae9148c7de383813d96182bcdf2636955096a19da1ffe97967",
        "0xe89937a887c84cf0422c4445780f573929fa8bdec06eaac39be91cd428a9c249",
        "0x0cb5b20122ce36c5d13058d8d6ec33b9fee729730f41a3adeacfb76dd8116ab7",
        "0x0c26dbd2461b7b5d")]
    [NonParallelizable]
    public virtual async Task Should_process_block_as_expected_V2(string latestValidHash, string blockHash,
        string stateRoot, string payloadId)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Shanghai.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
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
        Withdrawal[] withdrawals = [new() { Index = 1, AmountInGwei = 3, Address = TestItem.AddressB, ValidatorIndex = 2 }];
        var payloadAttrs = new
        {
            timestamp = timestamp.ToHexString(true),
            prevRandao = prevRandao.ToString(),
            suggestedFeeRecipient = feeRecipient.ToString(),
            withdrawals
        };
        string?[] @params = [chain.JsonSerializer.Serialize(fcuState), chain.JsonSerializer.Serialize(payloadAttrs)];
        string expectedPayloadId = payloadId;

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV2", @params);
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
                BaseFeePerGas = 0,
                Bloom = Bloom.Empty,
                GasUsed = 0,
                Hash = expectedBlockHash,
                MixHash = prevRandao,
                ReceiptsRoot = chain.BlockTree.Head!.ReceiptsRoot!,
                StateRoot = new(stateRoot),
            },
            Array.Empty<Transaction>(),
            Array.Empty<BlockHeader>(),
            withdrawals);
        GetPayloadV2Result expectedPayload = new(block, UInt256.Zero);

        response = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV2", expectedPayloadId);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        Assert.That(successResponse, Is.Not.Null);
        Assert.That(response, Is.EqualTo(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = expectedPayload
        })));

        response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV2",
            chain.JsonSerializer.Serialize(ExecutionPayload.Create(block)));
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

        response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV2", @params!);
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
    public virtual async Task forkchoiceUpdatedV1_should_fail_with_withdrawals()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(null, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpcModule = chain.EngineRpcModule;
        var fcuState = new
        {
            headBlockHash = chain.BlockTree.HeadHash.ToString(),
            safeBlockHash = chain.BlockTree.HeadHash.ToString(),
            finalizedBlockHash = chain.BlockTree.HeadHash.ToString(),
        };
        var payloadAttributes = new
        {
            timestamp = "0x0",
            prevRandao = Keccak.Zero.ToString(),
            suggestedFeeRecipient = Address.Zero.ToString(),
            withdrawals = Enumerable.Empty<Withdrawal>()
        };
        string[] @params =
        [
            chain.JsonSerializer.Serialize(fcuState),
            chain.JsonSerializer.Serialize(payloadAttributes)
        ];

        string response = await RpcTest.TestSerializedRequest(rpcModule, "engine_forkchoiceUpdatedV1", @params);
        JsonRpcErrorResponse? errorResponse = chain.JsonSerializer.Deserialize<JsonRpcErrorResponse>(response);

        Assert.That(errorResponse, Is.Not.Null);
        Assert.That(errorResponse!.Error, Is.Not.Null);
        Assert.That(errorResponse!.Error!.Code, Is.EqualTo(MergeErrorCodes.InvalidPayloadAttributes));
        Assert.That(errorResponse!.Error!.Message, Is.EqualTo("PayloadAttributesV1 expected"));
    }

    [TestCaseSource(nameof(GetWithdrawalValidationValues))]
    public virtual async Task forkchoiceUpdatedV2_should_validate_withdrawals((
        IReleaseSpec Spec,
        string ErrorMessage,
        Withdrawal[]? Withdrawals,
        string BlockHash,
        int ErrorCode
        ) input)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(input.Spec);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;
        var fcuState = new
        {
            headBlockHash = chain.BlockTree.HeadHash.ToString(),
            safeBlockHash = chain.BlockTree.HeadHash.ToString(),
            finalizedBlockHash = Keccak.Zero.ToString()
        };
        var payloadAttrs = new
        {
            timestamp = chain.BlockTree.Head!.Timestamp + 1,
            prevRandao = Keccak.Zero.ToString(),
            suggestedFeeRecipient = TestItem.AddressA.ToString(),
            withdrawals = input.Withdrawals
        };
        string[] @params =
        [
            chain.JsonSerializer.Serialize(fcuState),
            chain.JsonSerializer.Serialize(payloadAttrs)
        ];

        string response = await RpcTest.TestSerializedRequest(rpcModule, "engine_forkchoiceUpdatedV2", @params);
        JsonRpcErrorResponse? errorResponse = chain.JsonSerializer.Deserialize<JsonRpcErrorResponse>(response);

        Assert.That(errorResponse, Is.Not.Null);
        Assert.That(errorResponse!.Error, Is.Not.Null);
        Assert.That(errorResponse!.Error!.Code, Is.EqualTo(MergeErrorCodes.InvalidPayloadAttributes));
        Assert.That(errorResponse!.Error!.Message, Is.EqualTo(string.Format(input.ErrorMessage, "PayloadAttributes")));
    }

    [Test]
    public virtual async Task getPayloadV2_empty_block_should_have_zero_value()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;

        Hash256 startingHead = chain.BlockTree.HeadHash;

        ForkchoiceStateV1 forkchoiceState = new(startingHead, Keccak.Zero, startingHead);
        PayloadAttributes payload = new()
        {
            Timestamp = Timestamper.UnixTime.Seconds,
            SuggestedFeeRecipient = Address.Zero,
            PrevRandao = Keccak.Zero
        };
        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> forkchoiceResponse =
            rpc.engine_forkchoiceUpdatedV1(forkchoiceState, payload);

        byte[] payloadId = Bytes.FromHexString(forkchoiceResponse.Result.Data.PayloadId!);
        ResultWrapper<GetPayloadV2Result?> responseFirst = await rpc.engine_getPayloadV2(payloadId);
        Assert.That(responseFirst, Is.Not.Null);
        Assert.That(responseFirst.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(responseFirst.Data!.BlockValue, Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public virtual async Task getPayloadV2_received_fees_should_be_equal_to_block_value_in_result()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;

        Address feeRecipient = TestItem.AddressA;

        Hash256 startingHead = chain.BlockTree.HeadHash;
        uint count = 3;
        int value = 10;

        PrivateKey sender = TestItem.PrivateKeyB;
        Transaction[] transactions =
            BuildTransactions(chain, startingHead, sender, Address.Zero, count, value, out _, out _);

        Task blockImprovementWait = chain.WaitForImprovedBlock();

        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes()
                {
                    Timestamp = 100,
                    PrevRandao = TestItem.KeccakA,
                    SuggestedFeeRecipient = feeRecipient
                })
            .Result.Data.PayloadId!;

        UInt256 startingBalance = chain.ReadOnlyState.GetBalance(feeRecipient);

        await blockImprovementWait;
        GetPayloadV2Result getPayloadResult = (await rpc.engine_getPayloadV2(Bytes.FromHexString(payloadId))).Data!;

        ResultWrapper<PayloadStatusV1> executePayloadResult =
            await rpc.engine_newPayloadV1(getPayloadResult.ExecutionPayload);
        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        BlockHeader? payloadBlock = chain.BlockFinder.FindHeader(getPayloadResult.ExecutionPayload.BlockHash);
        UInt256 finalBalance = chain.StateReader.GetBalance(payloadBlock, feeRecipient);

        Assert.That((finalBalance - startingBalance), Is.EqualTo(getPayloadResult.BlockValue));
    }

    [Test]
    public virtual async Task getPayloadV2_should_fail_on_unknown_payload() =>
        await GetPayload_should_fail_on_unknown_payload(2);

    [TestCaseSource(nameof(GetPayloadWithdrawalsTestCases))]
    public virtual async Task
        getPayloadBodiesByHashV1_should_return_payload_bodies_in_order_of_request_block_hashes_and_null_for_unknown_hashes(
            Withdrawal[] withdrawals)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Shanghai.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload executionPayload1 = await SendNewBlockV2(rpc, chain, withdrawals);
        Transaction[] txs = BuildTransactions(
            chain, executionPayload1.BlockHash, TestItem.PrivateKeyA, TestItem.AddressB, 3, 0, out _, out _);

        chain.AddTransactions(txs);

        ExecutionPayload executionPayload2 = await BuildAndSendNewBlockV2(rpc, chain, true, withdrawals);
        Hash256[] blockHashes = [executionPayload1.BlockHash, TestItem.KeccakA, executionPayload2.BlockHash];
        IReadOnlyList<ExecutionPayloadBodyV1Result?> payloadBodies =
            rpc.engine_getPayloadBodiesByHashV1(blockHashes).Data;
        ExecutionPayloadBodyV1Result?[] expected = {
            new(Array.Empty<Transaction>(), withdrawals), null, new(txs, withdrawals)
        };

        Assert.That(JToken.Parse(chain.JsonSerializer.Serialize(payloadBodies)), Is.EqualTo(JToken.Parse(chain.JsonSerializer.Serialize(expected))).Using(JToken.EqualityComparer));
    }

    [TestCaseSource(nameof(GetPayloadWithdrawalsTestCases))]
    public virtual async Task
        getPayloadBodiesByRangeV1_should_return_payload_bodies_in_order_of_request_range_and_null_for_unknown_indexes(
            Withdrawal[] withdrawals)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Shanghai.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload executionPayload1 = await SendNewBlockV2(rpc, chain, withdrawals);
        Transaction[] txs = BuildTransactions(
            chain, executionPayload1.BlockHash, TestItem.PrivateKeyA, TestItem.AddressB, 3, 0, out _, out _);

        chain.AddTransactions(txs);

        await BuildAndSendNewBlockV2(rpc, chain, true, withdrawals);
        ExecutionPayload executionPayload2 = await BuildAndSendNewBlockV2(rpc, chain, false, withdrawals);

        await rpc.engine_forkchoiceUpdatedV2(new ForkchoiceStateV1(executionPayload2.BlockHash!,
            executionPayload2.BlockHash!, executionPayload2.BlockHash!));

        IReadOnlyList<ExecutionPayloadBodyV1Result?> payloadBodies =
            rpc.engine_getPayloadBodiesByRangeV1(1, 3).Result.Data;
        ExecutionPayloadBodyV1Result?[] expected = { new(txs, withdrawals) };

        Assert.That(JToken.Parse(chain.JsonSerializer.Serialize(payloadBodies)), Is.EqualTo(JToken.Parse(chain.JsonSerializer.Serialize(expected))).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task getPayloadBodiesByRangeV1_empty_response()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayloadBodyV1Result?> payloadBodies =
            rpc.engine_getPayloadBodiesByRangeV1(1, 1).Result.Data;
        ExecutionPayloadBodyV1Result?[] expected = [];

        Assert.That(payloadBodies, Is.EqualTo(expected));
    }

    [Test]
    public async Task getPayloadBodiesByRangeV1_should_fail_when_too_many_payloads_requested()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> result =
            rpc.engine_getPayloadBodiesByRangeV1(1, 1025);

        Assert.That(result.Result.ErrorCode, Is.EqualTo(MergeErrorCodes.TooLargeRequest));
    }

    [Test]
    public async Task getPayloadBodiesByHashV1_should_fail_when_too_many_payloads_requested()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256[] hashes = Enumerable.Repeat(TestItem.KeccakA, 1025).ToArray();
        Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> result =
            rpc.engine_getPayloadBodiesByHashV1(hashes);

        Assert.That(result.Result.ErrorCode, Is.EqualTo(MergeErrorCodes.TooLargeRequest));
    }

    [Test]
    public async Task getPayloadBodiesByRangeV1_should_fail_when_params_below_1()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> result =
            rpc.engine_getPayloadBodiesByRangeV1(0, 1);

        Assert.That(result.Result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));

        result = await rpc.engine_getPayloadBodiesByRangeV1(1, 0);

        Assert.That(result.Result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [TestCaseSource(nameof(GetPayloadWithdrawalsTestCases))]
    public virtual async Task getPayloadBodiesByRangeV1_should_return_canonical(Withdrawal[] withdrawals)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Shanghai.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload executionPayload1 = await SendNewBlockV2(rpc, chain, withdrawals);

        await rpc.engine_forkchoiceUpdatedV2(new ForkchoiceStateV1(executionPayload1.BlockHash,
            executionPayload1.BlockHash!, executionPayload1.BlockHash));

        Block head = chain.BlockTree.Head!;

        // First branch
        {
            Transaction[] txsA = BuildTransactions(
                chain, executionPayload1.BlockHash!, TestItem.PrivateKeyA, TestItem.AddressA, 1, 0, out _, out _);

            chain.AddTransactions(txsA);

            ExecutionPayload executionPayload2 = await BuildAndGetPayloadResultV2(
                rpc, chain, head.Hash!, head.Hash!, head.Hash!, 1001, Keccak.Zero, Address.Zero, withdrawals);
            ResultWrapper<PayloadStatusV1> execResult = await rpc.engine_newPayloadV2(executionPayload2);

            Assert.That(execResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));

            ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult = await rpc.engine_forkchoiceUpdatedV2(
                new ForkchoiceStateV1(executionPayload2.BlockHash!, head.Hash!, head.Hash!));

            Assert.That(fcuResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

            IReadOnlyList<ExecutionPayloadBodyV1Result?> payloadBodies =
                rpc.engine_getPayloadBodiesByRangeV1(1, 3).Result.Data;
            ExecutionPayloadBodyV1Result[] expected =
            [
                new(Array.Empty<Transaction>(), withdrawals), new(txsA, withdrawals)
            ];

            Assert.That(JToken.Parse(chain.JsonSerializer.Serialize(payloadBodies)), Is.EqualTo(JToken.Parse(chain.JsonSerializer.Serialize(expected))).Using(JToken.EqualityComparer));
        }

        // Second branch
        {
            Block newBlock = Build.A.Block
                .WithNumber(head.Number + 1)
                .WithParent(head)
                .WithNonce(0)
                .WithDifficulty(0)
                .WithStateRoot(head.StateRoot!)
                .WithBeneficiary(Build.An.Address.TestObject)
                .WithWithdrawals(withdrawals.ToArray())
                .TestObject;

            ResultWrapper<PayloadStatusV1> fcuResult = await rpc.engine_newPayloadV2(ExecutionPayload.Create(newBlock));

            Assert.That(fcuResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));

            await rpc.engine_forkchoiceUpdatedV2(
                new ForkchoiceStateV1(newBlock.Hash!, newBlock.Hash!, newBlock.Hash!));

            IReadOnlyList<ExecutionPayloadBodyV1Result?> payloadBodies =
                rpc.engine_getPayloadBodiesByRangeV1(1, 3).Result.Data;
            ExecutionPayloadBodyV1Result[] expected =
            [
                new(Array.Empty<Transaction>(), withdrawals), new(Array.Empty<Transaction>(), withdrawals)
            ];

            Assert.That(JToken.Parse(chain.JsonSerializer.Serialize(payloadBodies)), Is.EqualTo(JToken.Parse(chain.JsonSerializer.Serialize(expected))).Using(JToken.EqualityComparer));
        }
    }

    [TestCaseSource(nameof(PayloadBodiesByRangeNullTrimTestCases))]
    public async Task getPayloadBodiesByRangeV1_should_trim_trailing_null_bodies(
        (Func<CallInfo, Block?> Impl,
            IReadOnlyList<ExecutionPayloadBodyV1Result?> Outcome) input)
    {
        IBlockTree? blockTree = Substitute.For<IBlockTree>();
        IBlockStore? blockStore = Substitute.For<IBlockStore>();
        BlockDecoder blockDecoder = new();

        blockTree.Head.Returns(Build.A.Block.WithNumber(5).TestObject);
        blockTree.FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(i => GetHeader(input.Impl(i)));
        blockStore.GetRlp(Arg.Any<long>(), Arg.Any<Hash256>())
            .Returns(i =>
            {
                Block? block = input.Impl(i);
                return block is null ? null : blockDecoder.Encode(block).Bytes;
            });

        using MergeTestBlockchain chain = await CreateBlockchain(Shanghai.Instance, configurer: (builder) =>
            builder
                .AddSingleton<IBlockTree>(blockTree)
                .AddSingleton<IBlockStore>(blockStore)
                .AddSingleton(new TestBlockchain.Configuration()
                {
                    SuggestGenesisOnStart = false,
                }));

        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayloadBodyV1Result?> payloadBodies =
            rpc.engine_getPayloadBodiesByRangeV1(1, 5).Result.Data;

        Assert.That(JToken.Parse(chain.JsonSerializer.Serialize(payloadBodies)), Is.EqualTo(JToken.Parse(chain.JsonSerializer.Serialize(input.Outcome))).Using(JToken.EqualityComparer));

        static BlockHeader? GetHeader(Block? block)
        {
            if (block is null)
            {
                return null;
            }

            block.Header.Hash = block.GetOrCalculateHash();
            return block.Header;
        }
    }

    [Test]
    public async Task getPayloadBodiesByRangeV1_should_return_up_to_best_body_number()
    {
        IBlockTree? blockTree = Substitute.For<IBlockTree>();

        blockTree.Head.Returns(Build.A.Block.WithNumber(5).TestObject);

        using MergeTestBlockchain chain = await CreateBlockchain(Shanghai.Instance, configurer: (builder) => builder
            .AddSingleton<IBlockTree>(blockTree)
            .AddSingleton(new TestBlockchain.Configuration()
            {
                SuggestGenesisOnStart = false,
            }));

        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayloadBodyV1Result?> payloadBodies =
            rpc.engine_getPayloadBodiesByRangeV1(1, 7).Result.Data;

        Assert.That(payloadBodies.Count, Is.EqualTo(5));
    }

    [Test]
    public async Task PayloadBodiesV1DirectResponse_WriteToAsync_produces_valid_json()
    {
        Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
        Withdrawal[] withdrawals = CreateDirectResponseWithdrawals();

        PayloadBodiesV1DirectResponse response = new([
            new ExecutionPayloadBodyV1Result([transaction], withdrawals),
            null,
            new ExecutionPayloadBodyV1Result([], null)
        ]);

        await AssertStreamedJsonMatchesSerializer(response);
    }

    [Test]
    public virtual async Task newPayloadV1_should_fail_with_withdrawals()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(null, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpcModule = chain.EngineRpcModule;
        ExecutionPayload expectedPayload = new()
        {
            BaseFeePerGas = 0,
            BlockHash = Keccak.Zero,
            BlockNumber = 1,
            ExtraData = [],
            FeeRecipient = Address.Zero,
            GasLimit = 0,
            GasUsed = 0,
            LogsBloom = Bloom.Empty,
            ParentHash = Keccak.Zero,
            PrevRandao = Keccak.Zero,
            ReceiptsRoot = Keccak.Zero,
            StateRoot = Keccak.Zero,
            Timestamp = 0,
            Transactions = [],
            Withdrawals = []
        };

        string response = await RpcTest.TestSerializedRequest(rpcModule, "engine_newPayloadV1",
            chain.JsonSerializer.Serialize(expectedPayload));
        JsonRpcErrorResponse? errorResponse = chain.JsonSerializer.Deserialize<JsonRpcErrorResponse>(response);

        Assert.That(errorResponse, Is.Not.Null);
        Assert.That(errorResponse!.Error, Is.Not.Null);
        Assert.That(errorResponse!.Error!.Code, Is.EqualTo(ErrorCodes.InvalidParams));
        Assert.That(errorResponse!.Error!.Message, Is.EqualTo("ExecutionPayloadV1 expected"));
    }

    [TestCaseSource(nameof(GetWithdrawalValidationValues))]
    public virtual async Task newPayloadV2_should_validate_withdrawals((
        IReleaseSpec Spec,
        string ErrorMessage,
        Withdrawal[]? Withdrawals,
        string BlockHash,
        int ErrorCode
        ) input)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(input.Spec);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;
        Hash256 blockHash = new(input.BlockHash);
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 prevRandao = Keccak.Zero;
        Address feeRecipient = TestItem.AddressC;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        ExecutionPayload expectedPayload = new()
        {
            BaseFeePerGas = 0,
            BlockHash = blockHash,
            BlockNumber = 1,
            ExtraData = Bytes.FromHexString("0x4e65746865726d696e64"), // Nethermind
            FeeRecipient = feeRecipient,
            GasLimit = chain.BlockTree.Head!.GasLimit,
            GasUsed = 0,
            LogsBloom = Bloom.Empty,
            ParentHash = startingHead,
            PrevRandao = prevRandao,
            ReceiptsRoot = chain.BlockTree.Head!.ReceiptsRoot!,
            StateRoot = new("0xde9a4fd5deef7860dc840612c5e960c942b76a9b2e710504de9bab8289156491"),
            Timestamp = timestamp,
            Transactions = [],
            Withdrawals = input.Withdrawals
        };

        string response = await RpcTest.TestSerializedRequest(rpcModule, "engine_newPayloadV2",
            chain.JsonSerializer.Serialize(expectedPayload));
        JsonRpcErrorResponse? errorResponse = chain.JsonSerializer.Deserialize<JsonRpcErrorResponse>(response);

        Assert.That(errorResponse, Is.Not.Null);
        Assert.That(errorResponse.Error, Is.Not.Null);
        Assert.That(errorResponse.Error!.Code, Is.EqualTo(input.ErrorCode));
        Assert.That(errorResponse.Error!.Message, Is.EqualTo(string.Format(input.ErrorMessage, "ExecutionPayload")));
    }

    protected static IEnumerable<(
        IReleaseSpec Spec,
        string ErrorMessage,
        Withdrawal[]? Withdrawals,
        string BlockHash,
        int ErrorCode
        )> GetWithdrawalValidationValues()
    {
        yield return (
            Shanghai.Instance,
            "{0}V2 expected",
            null,
            "0x6817d4b48be0bc14f144cc242cdc47a5ccc40de34b9c3934acad45057369f576",
            ErrorCodes.InvalidParams);
        yield return (
            London.Instance,
            "{0}V1 expected",
            Array.Empty<Withdrawal>(),
            "0xaa4aa15951a28e6adab430a795e36a84649bbafb1257eda23e38b9131cbd3b98",
            ErrorCodes.InvalidParams);
    }

    [TestCaseSource(nameof(ZeroWithdrawalsTestCases))]
    public async Task executePayloadV2_works_correctly_when_0_withdrawals_applied((
        IReleaseSpec ReleaseSpec,
        Withdrawal[]? Withdrawals,
        bool IsValid) input)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(input.ReleaseSpec);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload executionPayload = CreateBlockRequest(chain,
            CreateParentBlockRequestOnHead(chain.BlockTree),
            TestItem.AddressD, input.Withdrawals);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV2(executionPayload);

        if (input.IsValid)
            Assert.That(resultWrapper.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        else
            Assert.That(resultWrapper.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [TestCaseSource(nameof(WithdrawalsTestCases))]
    public virtual async Task Can_apply_withdrawals_correctly(
        (Withdrawal[][] Withdrawals, (Address Account, UInt256 BalanceIncrease)[] ExpectedAccountIncrease) input)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Shanghai.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        // get initial balances
        List<UInt256> initialBalances = [];
        foreach ((Address Account, UInt256 BalanceIncrease) accountIncrease in input.ExpectedAccountIncrease)
        {
            UInt256 initialBalance =
                chain.StateReader.GetBalance(chain.BlockTree.Head!.Header, accountIncrease.Account);
            initialBalances.Add(initialBalance);
        }

        foreach (Withdrawal[] withdrawal in input.Withdrawals)
        {
            PayloadAttributes payloadAttributes = new()
            {
                Timestamp = chain.BlockTree.Head!.Timestamp + 1,
                PrevRandao = TestItem.KeccakH,
                SuggestedFeeRecipient = TestItem.AddressF,
                Withdrawals = withdrawal
            };
            ExecutionPayload payload =
                (await BuildAndGetPayloadResultV2(rpc, chain, payloadAttributes))?.ExecutionPayload!;
            ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV2(payload!);
            Assert.That(resultWrapper.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            ResultWrapper<ForkchoiceUpdatedV1Result> resultFcu = await rpc.engine_forkchoiceUpdatedV2(
                new(payload.BlockHash, payload.BlockHash, payload.BlockHash));
            Assert.That(resultFcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        }

        // check balance increase
        for (int index = 0; index < input.ExpectedAccountIncrease.Length; index++)
        {
            (Address Account, UInt256 BalanceIncrease) accountIncrease = input.ExpectedAccountIncrease[index];
            UInt256 currentBalance =
                chain.StateReader.GetBalance(chain.BlockTree.Head!.Header!, accountIncrease.Account);
            Assert.That(currentBalance, Is.EqualTo(accountIncrease.BalanceIncrease + initialBalances[index]));
        }
    }

    [Test]
    public virtual async Task Should_handle_withdrawals_transition_when_Shanghai_fork_activated()
    {
        // Shanghai fork, ForkActivation.Timestamp = 3
        CustomSpecProvider specProvider = new(
            (new ForkActivation(0, null), ArrowGlacier.Instance),
            (new ForkActivation(0, 3), Shanghai.Instance)
        );

        // Genesis, Timestamp = 1
        using MergeTestBlockchain chain = await CreateBlockchain(specProvider);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        // Block without withdrawals, Timestamp = 2
        ExecutionPayload executionPayload =
            CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV2(executionPayload);
        Assert.That(resultWrapper.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        // Block with withdrawals, Timestamp = 3
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = chain.BlockTree.Head!.Timestamp + 2,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressF,
            Withdrawals = new[] { TestItem.WithdrawalA_1Eth }
        };
        ExecutionPayload payloadWithWithdrawals =
            (await BuildAndGetPayloadResultV2(rpc, chain, payloadAttributes))?.ExecutionPayload!;
        ResultWrapper<PayloadStatusV1> resultWithWithdrawals = await rpc.engine_newPayloadV2(payloadWithWithdrawals!);

        Assert.That(resultWithWithdrawals.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult = await rpc.engine_forkchoiceUpdatedV2(
            new(payloadWithWithdrawals.BlockHash, payloadWithWithdrawals.BlockHash, payloadWithWithdrawals.BlockHash));

        Assert.That(fcuResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
    }

    [Test]
    public void Should_print_payload_attributes_as_expected()
    {
        PayloadAttributes attrs = new()
        {
            Timestamp = 1,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressF,
            Withdrawals = new[] { TestItem.WithdrawalA_1Eth }
        };

        Assert.That(attrs.ToString(), Is.EqualTo($"PayloadAttributes {{Timestamp: {attrs.Timestamp}, PrevRandao: {attrs.PrevRandao}, SuggestedFeeRecipient: {attrs.SuggestedFeeRecipient}, Withdrawals count: {attrs.Withdrawals.Length}}}"));
    }

    [TestCaseSource(nameof(PayloadIdTestCases))]
    public void Should_compute_payload_id_with_withdrawals((Withdrawal[]? Withdrawals, string PayloadId) input)
    {
        BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
        PayloadAttributes payloadAttributes = new()
        {
            PrevRandao = Keccak.Zero,
            SuggestedFeeRecipient = Address.Zero,
            Timestamp = 0,
            Withdrawals = input.Withdrawals
        };

        string payloadId = payloadAttributes.GetPayloadId(blockHeader);

        Assert.That(payloadId, Is.EqualTo(input.PayloadId));
    }

    private static async Task<GetPayloadV2Result> BuildAndGetPayloadResultV2(
        IEngineRpcModule rpc, MergeTestBlockchain chain, PayloadAttributes payloadAttributes)
    {
        Hash256 currentHeadHash = chain.BlockTree.HeadHash;
        ForkchoiceStateV1 forkchoiceState = new(currentHeadHash, currentHeadHash, currentHeadHash);
        string payloadId = rpc.engine_forkchoiceUpdatedV2(forkchoiceState, payloadAttributes).Result.Data.PayloadId!;
        ResultWrapper<GetPayloadV2Result?> getPayloadResult =
            await rpc.engine_getPayloadV2(Bytes.FromHexString(payloadId));
        return getPayloadResult.Data!;
    }

    protected static IEnumerable<TestCaseData> WithdrawalsTestCases()
    {
        static TestCaseData Case(string name, Withdrawal[][] withdrawals, (Address, UInt256)[] expectedAccountIncrease) =>
            new TestCaseData(((Withdrawal[][] Withdrawals, (Address, UInt256)[] ExpectedAccountIncrease))(withdrawals, expectedAccountIncrease))
                .SetName(name);

        yield return Case("EmptyWithdrawals", [Array.Empty<Withdrawal>()], Array.Empty<(Address, UInt256)>());
        yield return Case("TwoAccountsSinglePayload", [[TestItem.WithdrawalA_1Eth, TestItem.WithdrawalB_2Eth]],
            [(TestItem.AddressA, 1.Ether), (TestItem.AddressB, 2.Ether)]);
        yield return Case("SameAccountSinglePayload", [[TestItem.WithdrawalA_1Eth, TestItem.WithdrawalA_1Eth]],
            [(TestItem.AddressA, 2.Ether), (TestItem.AddressB, 0.Ether)]);
        yield return Case("SameAccountMultiplePayloads", [[TestItem.WithdrawalA_1Eth, TestItem.WithdrawalA_1Eth], [TestItem.WithdrawalA_1Eth]],
            [(TestItem.AddressA, 3.Ether), (TestItem.AddressB, 0.Ether)]);
        yield return Case("MixedMultiplePayloads", [
                [TestItem.WithdrawalA_1Eth, TestItem.WithdrawalA_1Eth], // 1st payload
                [TestItem.WithdrawalA_1Eth], // 2nd payload
                [], // 3rd payload
                [TestItem.WithdrawalA_1Eth, TestItem.WithdrawalC_3Eth], // 4th payload
                [TestItem.WithdrawalB_2Eth, TestItem.WithdrawalF_6Eth] // 5th payload
            ],
            [
                (TestItem.AddressA, 4.Ether), (TestItem.AddressB, 2.Ether), (TestItem.AddressC, 3.Ether),
                (TestItem.AddressF, 6.Ether)
            ]);
    }

    protected static IEnumerable<IList<Withdrawal>> GetPayloadWithdrawalsTestCases()
    {
        yield return new[]
        {
            new Withdrawal { Index = 1, ValidatorIndex = 1 }, new Withdrawal { Index = 2, ValidatorIndex = 2 }
        };
    }

    private async Task<ExecutionPayload> BuildAndGetPayloadResultV2(
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
            Withdrawals = withdrawals
        };
        string? payloadId = rpc.engine_forkchoiceUpdatedV2(forkchoiceState, payloadAttributes).Result.Data.PayloadId;

        await blockImprovementWait;

        ResultWrapper<ExecutionPayload?> getPayloadResult =
            await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId!));

        return getPayloadResult.Data!;
    }

    private async Task<ExecutionPayload> BuildAndSendNewBlockV2(
        IEngineRpcModule rpc,
        MergeTestBlockchain chain,
        bool waitForBlockImprovement,
        Withdrawal[]? withdrawals)
    {
        Hash256 head = chain.BlockTree.HeadHash;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 random = Keccak.Zero;
        Address feeRecipient = Address.Zero;
        ExecutionPayload executionPayload = await BuildAndGetPayloadResultV2(rpc, chain, head,
            Keccak.Zero, head, timestamp, random, feeRecipient, withdrawals, waitForBlockImprovement);
        ResultWrapper<PayloadStatusV1> executePayloadResult =
            await rpc.engine_newPayloadV2(executionPayload);
        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        return executionPayload;
    }

    private async Task<ExecutionPayload> SendNewBlockV2(IEngineRpcModule rpc, MergeTestBlockchain chain,
        Withdrawal[]? withdrawals)
    {
        ExecutionPayload executionPayload = CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals);
        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV2(executionPayload);

        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        return executionPayload;
    }

    protected static IEnumerable<TestCaseData> ZeroWithdrawalsTestCases()
    {
        static TestCaseData Case(string name, IReleaseSpec releaseSpec, Withdrawal[]? withdrawals, bool isValid) =>
            new TestCaseData(((IReleaseSpec ReleaseSpec, Withdrawal[]? Withdrawals, bool IsValid))(releaseSpec, withdrawals, isValid))
                .SetName(name);

        yield return Case("LondonNullWithdrawals", London.Instance, null, true);
        yield return Case("ShanghaiNullWithdrawals", Shanghai.Instance, null, false);
        yield return Case("LondonEmptyWithdrawals", London.Instance, Array.Empty<Withdrawal>(), false);
        yield return Case("ShanghaiEmptyWithdrawals", Shanghai.Instance, Array.Empty<Withdrawal>(), true);
        yield return Case("LondonNonEmptyWithdrawals", London.Instance, new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalB_2Eth }, false);
    }

    protected static IEnumerable<TestCaseData> PayloadBodiesByRangeNullTrimTestCases()
    {
        static TestCaseData Case(string name, Func<CallInfo, Block?> blockFinder, IReadOnlyList<ExecutionPayloadBodyV1Result?> expectedBodies) =>
            new TestCaseData(((Func<CallInfo, Block?> BlockFinder, IReadOnlyList<ExecutionPayloadBodyV1Result?> ExpectedBodies))(blockFinder, expectedBodies))
                .SetName(name);

        static Block BuildBlock(CallInfo i) => Build.A.Block.WithNumber(i.ArgAt<long>(0)).TestObject;
        ExecutionPayloadBodyV1Result result = new(Array.Empty<Transaction>(), null);

        yield return Case("AllMissing", _ => null, (IReadOnlyList<ExecutionPayloadBodyV1Result?>)[null, null, null, null, null]);
        yield return Case("EveryOtherBlockMissing", i => i.ArgAt<long>(0) % 2 == 0 ? BuildBlock(i) : null, (IReadOnlyList<ExecutionPayloadBodyV1Result?>)[null, result, null, result, null]);
        yield return Case("AllPresent", BuildBlock, (IReadOnlyList<ExecutionPayloadBodyV1Result?>)[result, result, result, result, result]);
    }

    protected static IEnumerable<TestCaseData> PayloadIdTestCases()
    {
        static TestCaseData Case(string name, Withdrawal[]? withdrawals, string payloadId) =>
            new TestCaseData(((Withdrawal[]? Withdrawals, string PayloadId))(withdrawals, payloadId))
                .SetName(name);

        yield return Case("NullWithdrawals", null, "0xe3b6f7433feedc38");
        yield return Case("EmptyWithdrawals", Array.Empty<Withdrawal>(), "0xf74921b673b2e08e");
        yield return Case("OneWithdrawal", [Build.A.Withdrawal.TestObject], "0xe0d0b996245ec3a6");
    }
}
