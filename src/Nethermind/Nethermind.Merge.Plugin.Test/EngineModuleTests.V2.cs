// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public virtual async Task Should_process_block_as_expected_V2()
    {
        using MergeTestBlockchain chain = await CreateShanghaiBlockChain(new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        Keccak prevRandao = Keccak.Zero;
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
            withdrawals
        };
        string?[] @params = new string?[]
        {
            chain.JsonSerializer.Serialize(fcuState),
            chain.JsonSerializer.Serialize(payloadAttrs)
        };
        string expectedPayloadId = "0x6454408c425ddd96";

        string response = RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV2", @params!);
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
                    LatestValidHash = new("0x1c53bdbf457025f80c6971a9cf50986974eed02f0a9acaeeb49cafef10efd133"),
                    Status = PayloadStatus.Valid,
                    ValidationError = null
                }
            }
        }));

        Keccak blockHash = new("0x6d8a107ccab7a785de89f58db49064ee091df5d2b6306fe55db666e75a0e9f68");
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
                Hash = blockHash,
                MixHash = prevRandao,
                ReceiptsRoot = chain.BlockTree.Head!.ReceiptsRoot!,
                StateRoot = new("0x03e662d795ee2234c492ca4a08de03b1d7e3e0297af81a76582e16de75cdfc51"),
            },
            Array.Empty<Transaction>(),
            Array.Empty<BlockHeader>(),
            withdrawals
        );
        GetPayloadV2Result expectedPayload = new(block, UInt256.Zero);

        response = RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV2", expectedPayloadId);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        successResponse.Should().NotBeNull();
        response.Should().Be(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = expectedPayload
        }));

        response = RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV2",
            chain.JsonSerializer.Serialize(new ExecutionPayload(block)));
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        successResponse.Should().NotBeNull();
        response.Should().Be(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = new PayloadStatusV1
            {
                LatestValidHash = blockHash,
                Status = PayloadStatus.Valid,
                ValidationError = null
            }
        }));

        fcuState = new
        {
            headBlockHash = blockHash.ToString(true),
            safeBlockHash = blockHash.ToString(true),
            finalizedBlockHash = startingHead.ToString(true)
        };
        @params = new[]
        {
            chain.JsonSerializer.Serialize(fcuState),
            null
        };

        response = RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV2", @params!);
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
                    LatestValidHash = blockHash,
                    Status = PayloadStatus.Valid,
                    ValidationError = null
                }
            }
        }));
    }

    [Test]
    public virtual async Task forkchoiceUpdatedV1_should_fail_with_withdrawals()
    {
        using MergeTestBlockchain chain = await CreateBlockChain(new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        var fcuState = new
        {
            headBlockHash = Keccak.Zero.ToString(),
            safeBlockHash = Keccak.Zero.ToString(),
            finalizedBlockHash = Keccak.Zero.ToString()
        };
        var payloadAttrs = new
        {
            timestamp = "0x0",
            prevRandao = Keccak.Zero.ToString(),
            suggestedFeeRecipient = Address.Zero.ToString(),
            withdrawals = Enumerable.Empty<Withdrawal>()
        };
        string[] @params = new[]
        {
            chain.JsonSerializer.Serialize(fcuState),
            chain.JsonSerializer.Serialize(payloadAttrs)
        };

        string response = RpcTest.TestSerializedRequest(rpcModule, "engine_forkchoiceUpdatedV1", @params);
        JsonRpcErrorResponse? errorResponse = chain.JsonSerializer.Deserialize<JsonRpcErrorResponse>(response);

        errorResponse.Should().NotBeNull();
        errorResponse!.Error.Should().NotBeNull();
        errorResponse!.Error!.Code.Should().Be(ErrorCodes.InvalidParams);
        errorResponse!.Error!.Message.Should().Be("PayloadAttributesV1 expected");
    }

    [TestCaseSource(nameof(GetWithdrawalValidationValues))]
    public virtual async Task forkchoiceUpdatedV2_should_validate_withdrawals((
        IReleaseSpec Spec,
        string ErrorMessage,
        IEnumerable<Withdrawal>? Withdrawals,
        string BlockHash
        ) input)
    {
        using MergeTestBlockchain chain = await CreateBlockChain(null, null, input.Spec);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        var fcuState = new
        {
            headBlockHash = chain.BlockTree.HeadHash.ToString(),
            safeBlockHash = chain.BlockTree.HeadHash.ToString(),
            finalizedBlockHash = Keccak.Zero.ToString()
        };
        var payloadAttrs = new
        {
            timestamp = Timestamper.UnixTime.Seconds.ToHexString(true),
            prevRandao = Keccak.Zero.ToString(),
            suggestedFeeRecipient = TestItem.AddressA.ToString(),
            withdrawals = input.Withdrawals
        };
        string[] @params = new[]
        {
            chain.JsonSerializer.Serialize(fcuState),
            chain.JsonSerializer.Serialize(payloadAttrs)
        };

        string response = RpcTest.TestSerializedRequest(rpcModule, "engine_forkchoiceUpdatedV2", @params);
        JsonRpcErrorResponse? errorResponse = chain.JsonSerializer.Deserialize<JsonRpcErrorResponse>(response);

        errorResponse.Should().NotBeNull();
        errorResponse!.Error.Should().NotBeNull();
        errorResponse!.Error!.Code.Should().Be(ErrorCodes.InvalidParams);
        errorResponse!.Error!.Message.Should().Be(string.Format(input.ErrorMessage, "PayloadAttributes"));
    }

    [Test]
    public async Task getPayloadV2_empty_block_should_have_zero_value()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);

        Keccak startingHead = chain.BlockTree.HeadHash;

        ForkchoiceStateV1 forkchoiceState = new(startingHead, Keccak.Zero, startingHead);
        PayloadAttributes payload = new() { Timestamp = Timestamper.UnixTime.Seconds, SuggestedFeeRecipient = Address.Zero, PrevRandao = Keccak.Zero };
        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> forkchoiceResponse = rpc.engine_forkchoiceUpdatedV1(forkchoiceState, payload);

        byte[] payloadId = Bytes.FromHexString(forkchoiceResponse.Result.Data.PayloadId!);
        ResultWrapper<GetPayloadV2Result?> responseFirst = await rpc.engine_getPayloadV2(payloadId);
        responseFirst.Should().NotBeNull();
        responseFirst.Result.ResultType.Should().Be(ResultType.Success);
        responseFirst.Data!.BlockValue.Should().Be(0);
    }

    [Test]
    public async Task getPayloadV2_received_fees_should_be_equal_to_block_value_in_result()
    {
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);

        Address feeRecipient = TestItem.AddressA;

        Keccak startingHead = chain.BlockTree.HeadHash;
        uint count = 3;
        int value = 10;

        PrivateKey sender = TestItem.PrivateKeyB;
        Transaction[] transactions = BuildTransactions(chain, startingHead, sender, Address.Zero, count, value, out _, out _);

        chain.AddTransactions(transactions);
        chain.PayloadPreparationService!.BlockImproved += (_, _) => { blockImprovementLock.Release(1); };

        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes() { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = feeRecipient })
            .Result.Data.PayloadId!;

        UInt256 startingBalance = chain.StateReader.GetBalance(chain.State.StateRoot, feeRecipient);

        await blockImprovementLock.WaitAsync(10000);
        GetPayloadV2Result getPayloadResult = (await rpc.engine_getPayloadV2(Bytes.FromHexString(payloadId))).Data!;

        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult.ExecutionPayload);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        UInt256 finalBalance = chain.StateReader.GetBalance(getPayloadResult.ExecutionPayload.StateRoot, feeRecipient);

        (finalBalance - startingBalance).Should().Be(getPayloadResult.BlockValue);
    }

    [Test]
    public async Task getPayloadV2_should_fail_on_unknown_payload()
    {
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);

        byte[] payloadId = Bytes.FromHexString("0x0");
        ResultWrapper<GetPayloadV2Result?> responseFirst = await rpc.engine_getPayloadV2(payloadId);
        responseFirst.Should().NotBeNull();
        responseFirst.Result.ResultType.Should().Be(ResultType.Failure);
        responseFirst.ErrorCode.Should().Be(-38001);
    }

    [Test]
    public virtual async Task newPayloadV1_should_fail_with_withdrawals()
    {
        using MergeTestBlockchain chain = await CreateBlockChain(new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        ExecutionPayload expectedPayload = new()
        {
            BaseFeePerGas = 0,
            BlockHash = Keccak.Zero,
            BlockNumber = 1,
            ExtraData = Array.Empty<byte>(),
            FeeRecipient = Address.Zero,
            GasLimit = 0,
            GasUsed = 0,
            LogsBloom = Bloom.Empty,
            ParentHash = Keccak.Zero,
            PrevRandao = Keccak.Zero,
            ReceiptsRoot = Keccak.Zero,
            StateRoot = Keccak.Zero,
            Timestamp = 0,
            Transactions = Array.Empty<byte[]>(),
            Withdrawals = Enumerable.Empty<Withdrawal>()
        };

        string response = RpcTest.TestSerializedRequest(rpcModule, "engine_newPayloadV1",
            chain.JsonSerializer.Serialize(expectedPayload));
        JsonRpcErrorResponse? errorResponse = chain.JsonSerializer.Deserialize<JsonRpcErrorResponse>(response);

        errorResponse.Should().NotBeNull();
        errorResponse!.Error.Should().NotBeNull();
        errorResponse!.Error!.Code.Should().Be(ErrorCodes.InvalidParams);
        errorResponse!.Error!.Message.Should().Be("ExecutionPayloadV1 expected");
    }

    [TestCaseSource(nameof(GetWithdrawalValidationValues))]
    public virtual async Task newPayloadV2_should_validate_withdrawals((
        IReleaseSpec Spec,
        string ErrorMessage,
        IEnumerable<Withdrawal>? Withdrawals,
        string BlockHash
        ) input)
    {
        using MergeTestBlockchain chain = await CreateBlockChain(null, null, input.Spec);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        Keccak blockHash = new(input.BlockHash);
        Keccak startingHead = chain.BlockTree.HeadHash;
        Keccak prevRandao = Keccak.Zero;
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
            Transactions = Array.Empty<byte[]>(),
            Withdrawals = input.Withdrawals
        };

        string response = RpcTest.TestSerializedRequest(rpcModule, "engine_newPayloadV2",
            chain.JsonSerializer.Serialize(expectedPayload));
        JsonRpcErrorResponse? errorResponse = chain.JsonSerializer.Deserialize<JsonRpcErrorResponse>(response);

        errorResponse.Should().NotBeNull();
        errorResponse!.Error.Should().NotBeNull();
        errorResponse!.Error!.Code.Should().Be(ErrorCodes.InvalidParams);
        errorResponse!.Error!.Message.Should().Be(string.Format(input.ErrorMessage, "ExecutionPayload"));
    }

    protected static IEnumerable<(
        IReleaseSpec spec,
        string ErrorMessage,
        IEnumerable<Withdrawal>? Withdrawals,
        string blockHash
        )> GetWithdrawalValidationValues()
    {
        yield return (
            Shanghai.Instance,
            "{0}V2 expected",
            null,
            "0x6817d4b48be0bc14f144cc242cdc47a5ccc40de34b9c3934acad45057369f576");
        yield return (
            London.Instance,
            "{0}V1 expected",
            Enumerable.Empty<Withdrawal>(),
            "0xaa4aa15951a28e6adab430a795e36a84649bbafb1257eda23e38b9131cbd3b98");
    }

    [TestCaseSource(nameof(ZeroWithdrawalsTestCases))]
    public async Task executePayloadV2_works_correctly_when_0_withdrawals_applied((
        IReleaseSpec ReleaseSpec,
        Withdrawal[]? Withdrawals,
        bool IsValid) input)
    {
        using MergeTestBlockchain chain = await CreateBlockChain(null, null, input.ReleaseSpec);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        ExecutionPayload executionPayload = CreateBlockRequest(CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, input.Withdrawals);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV2(executionPayload);

        if (input.IsValid)
            resultWrapper.Data.Status.Should().Be(PayloadStatus.Valid);
        else
            resultWrapper.ErrorCode.Should().Be(ErrorCodes.InvalidParams);
    }

    protected static IEnumerable<(
        IReleaseSpec releaseSpec,
        Withdrawal[]? Withdrawals,
        bool isValid
        )> ZeroWithdrawalsTestCases()
    {
        yield return (London.Instance, null, true);
        yield return (Shanghai.Instance, null, false);
        yield return (London.Instance, Array.Empty<Withdrawal>(), false);
        yield return (Shanghai.Instance, Array.Empty<Withdrawal>(), true);
        yield return (London.Instance, new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalB_2Eth }, false);
    }

    [TestCaseSource(nameof(WithdrawalsTestCases))]
    public async Task Can_apply_withdrawals_correctly((Withdrawal[][] Withdrawals, (Address Account, UInt256 BalanceIncrease)[] ExpectedAccountIncrease) input)
    {
        using MergeTestBlockchain chain = await CreateShanghaiBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);

        // get initial balances
        List<UInt256> initialBalances = new();
        foreach ((Address Account, UInt256 BalanceIncrease) accountIncrease in input.ExpectedAccountIncrease)
        {
            UInt256 initialBalance = chain.StateReader.GetBalance(chain.BlockTree.Head!.StateRoot!, accountIncrease.Account);
            initialBalances.Add(initialBalance);
        }

        foreach (Withdrawal[] withdrawal in input.Withdrawals)
        {
            PayloadAttributes payloadAttributes = new() { Timestamp = chain.BlockTree.Head!.Timestamp + 1, PrevRandao = TestItem.KeccakH, SuggestedFeeRecipient = TestItem.AddressF, Withdrawals = withdrawal };
            ExecutionPayload payload = (await BuildAndGetPayloadResultV2(rpc, chain, payloadAttributes))?.ExecutionPayload!;
            ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV2(payload!);
            resultWrapper.Data.Status.Should().Be(PayloadStatus.Valid);
            ResultWrapper<ForkchoiceUpdatedV1Result> resultFcu = await rpc.engine_forkchoiceUpdatedV2(
                new(payload.BlockHash, payload.BlockHash, payload.BlockHash));
            resultFcu.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
        }

        // check balance increase
        for (int index = 0; index < input.ExpectedAccountIncrease.Length; index++)
        {
            (Address Account, UInt256 BalanceIncrease) accountIncrease = input.ExpectedAccountIncrease[index];
            UInt256 currentBalance = chain.StateReader.GetBalance(chain.BlockTree.Head!.StateRoot!, accountIncrease.Account);
            currentBalance.Should().Be(accountIncrease.BalanceIncrease + initialBalances[index]);
        }
    }

    [Test]
    public async Task Should_handle_withdrawals_transition_when_Shanghai_fork_activated()
    {
        // Shanghai fork, ForkActivation.Timestamp = 3
        CustomSpecProvider specProvider = new(
            (new ForkActivation(0, null), ArrowGlacier.Instance),
            (new ForkActivation(0, 3), Shanghai.Instance)
            );

        // Genesis, Timestamp = 1
        using MergeTestBlockchain chain = await CreateBlockChain(specProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);

        // Block without withdrawals, Timestamp = 2
        ExecutionPayload executionPayload = CreateBlockRequest(CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV2(executionPayload);
        resultWrapper.Data.Status.Should().Be(PayloadStatus.Valid);

        // Block with withdrawals, Timestamp = 3
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = chain.BlockTree.Head!.Timestamp + 2,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressF,
            Withdrawals = new[] { TestItem.WithdrawalA_1Eth }
        };
        ExecutionPayload payloadWithWithdrawals = (await BuildAndGetPayloadResultV2(rpc, chain, payloadAttributes))?.ExecutionPayload!;
        ResultWrapper<PayloadStatusV1> resultWithWithdrawals = await rpc.engine_newPayloadV2(payloadWithWithdrawals!);

        resultWithWithdrawals.Data.Status.Should().Be(PayloadStatus.Valid);

        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult = await rpc.engine_forkchoiceUpdatedV2(
            new(payloadWithWithdrawals.BlockHash, payloadWithWithdrawals.BlockHash, payloadWithWithdrawals.BlockHash));

        fcuResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
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

        attrs.ToString().Should().Be(
            $"PayloadAttributes {{Timestamp: {attrs.Timestamp}, PrevRandao: {attrs.PrevRandao}, SuggestedFeeRecipient: {attrs.SuggestedFeeRecipient}, Withdrawals count: {attrs.Withdrawals.Count}}}");
    }

    private static async Task<GetPayloadV2Result> BuildAndGetPayloadResultV2(
        IEngineRpcModule rpc, MergeTestBlockchain chain, PayloadAttributes payloadAttributes)
    {
        Keccak currentHeadHash = chain.BlockTree.HeadHash;
        ForkchoiceStateV1 forkchoiceState = new(currentHeadHash, currentHeadHash, currentHeadHash);
        string payloadId = rpc.engine_forkchoiceUpdatedV2(forkchoiceState, payloadAttributes).Result.Data.PayloadId!;
        ResultWrapper<GetPayloadV2Result?> getPayloadResult =
            await rpc.engine_getPayloadV2(Bytes.FromHexString(payloadId));
        return getPayloadResult.Data!;
    }

    protected static IEnumerable<(
        Withdrawal[][] Withdrawals, // withdrawals per payload
        (Address, UInt256)[] expectedAccountIncrease)> WithdrawalsTestCases()
    {
        yield return (new[] { Array.Empty<Withdrawal>() }, Array.Empty<(Address, UInt256)>());
        yield return (new[] { new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalB_2Eth } }, new[] { (TestItem.AddressA, 1.Ether()), (TestItem.AddressB, 2.Ether()) });
        yield return (new[] { new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalA_1Eth } }, new[] { (TestItem.AddressA, 2.Ether()), (TestItem.AddressB, 0.Ether()) });
        yield return (new[] { new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalA_1Eth }, new[] { TestItem.WithdrawalA_1Eth } }, new[] { (TestItem.AddressA, 3.Ether()), (TestItem.AddressB, 0.Ether()) });
        yield return (new[]
        {
            new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalA_1Eth }, // 1st payload
            new[] { TestItem.WithdrawalA_1Eth }, // 2nd payload
            Array.Empty<Withdrawal>(), // 3rd payload
            new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalC_3Eth }, // 4th payload
            new[] { TestItem.WithdrawalB_2Eth, TestItem.WithdrawalF_6Eth }, // 5th payload
        }, new[] { (TestItem.AddressA, 4.Ether()), (TestItem.AddressB, 2.Ether()), (TestItem.AddressC, 3.Ether()), (TestItem.AddressF, 6.Ether()) });
    }
}
