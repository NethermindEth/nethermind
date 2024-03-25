// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
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
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [TestCase(
        "0x1c53bdbf457025f80c6971a9cf50986974eed02f0a9acaeeb49cafef10efd133",
        "0x6d8a107ccab7a785de89f58db49064ee091df5d2b6306fe55db666e75a0e9f68",
        "0x03e662d795ee2234c492ca4a08de03b1d7e3e0297af81a76582e16de75cdfc51",
        "0xabd41416f2618ad0")]
    public virtual async Task Should_process_block_as_expected_V2(string latestValidHash, string blockHash,
        string stateRoot, string payloadId)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Shanghai.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
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
            withdrawals
        };
        string?[] @params = new string?[]
        {
            chain.JsonSerializer.Serialize(fcuState), chain.JsonSerializer.Serialize(payloadAttrs)
        };
        string expectedPayloadId = payloadId;

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV2", @params!);
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
            withdrawals
        );
        GetPayloadV2Result expectedPayload = new(block, UInt256.Zero);

        response = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV2", expectedPayloadId);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        successResponse.Should().NotBeNull();
        response.Should().Be(chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = successResponse.Id,
            Result = expectedPayload
        }));

        response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV2",
            chain.JsonSerializer.Serialize(new ExecutionPayload(block)));
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

        response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV2", @params!);
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
    public virtual async Task forkchoiceUpdatedV1_should_fail_with_withdrawals()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(null, new MergeConfig { TerminalTotalDifficulty = "0" });
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
            chain.JsonSerializer.Serialize(fcuState), chain.JsonSerializer.Serialize(payloadAttrs)
        };

        string response = await RpcTest.TestSerializedRequest(rpcModule, "engine_forkchoiceUpdatedV1", @params);
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
        Withdrawal[]? Withdrawals,
        string BlockHash
        ) input)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(input.Spec);
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
            chain.JsonSerializer.Serialize(fcuState), chain.JsonSerializer.Serialize(payloadAttrs)
        };

        string response = await RpcTest.TestSerializedRequest(rpcModule, "engine_forkchoiceUpdatedV2", @params);
        JsonRpcErrorResponse? errorResponse = chain.JsonSerializer.Deserialize<JsonRpcErrorResponse>(response);

        errorResponse.Should().NotBeNull();
        errorResponse!.Error.Should().NotBeNull();
        errorResponse!.Error!.Code.Should().Be(ErrorCodes.InvalidParams);
        errorResponse!.Error!.Message.Should().Be(string.Format(input.ErrorMessage, "PayloadAttributes"));
    }

    [Test]
    public virtual async Task getPayloadV2_empty_block_should_have_zero_value()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);

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
        responseFirst.Should().NotBeNull();
        responseFirst.Result.ResultType.Should().Be(ResultType.Success);
        responseFirst.Data!.BlockValue.Should().Be(0);
    }

    [Test]
    public virtual async Task getPayloadV2_received_fees_should_be_equal_to_block_value_in_result()
    {
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);

        Address feeRecipient = TestItem.AddressA;

        Hash256 startingHead = chain.BlockTree.HeadHash;
        uint count = 3;
        int value = 10;

        PrivateKey sender = TestItem.PrivateKeyB;
        Transaction[] transactions =
            BuildTransactions(chain, startingHead, sender, Address.Zero, count, value, out _, out _);

        chain.AddTransactions(transactions);
        chain.PayloadPreparationService!.BlockImproved += (_, _) => { blockImprovementLock.Release(1); };

        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes()
                {
                    Timestamp = 100,
                    PrevRandao = TestItem.KeccakA,
                    SuggestedFeeRecipient = feeRecipient
                })
            .Result.Data.PayloadId!;

        UInt256 startingBalance = chain.StateReader.GetBalance(chain.State.StateRoot, feeRecipient);

        await blockImprovementLock.WaitAsync(10000);
        GetPayloadV2Result getPayloadResult = (await rpc.engine_getPayloadV2(Bytes.FromHexString(payloadId))).Data!;

        ResultWrapper<PayloadStatusV1> executePayloadResult =
            await rpc.engine_newPayloadV1(getPayloadResult.ExecutionPayload);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        UInt256 finalBalance = chain.StateReader.GetBalance(getPayloadResult.ExecutionPayload.StateRoot, feeRecipient);

        (finalBalance - startingBalance).Should().Be(getPayloadResult.BlockValue);
    }

    [Test]
    public virtual async Task getPayloadV2_should_fail_on_unknown_payload()
    {
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);

        byte[] payloadId = Bytes.FromHexString("0x0");
        ResultWrapper<GetPayloadV2Result?> responseFirst = await rpc.engine_getPayloadV2(payloadId);
        responseFirst.Should().NotBeNull();
        responseFirst.Result.ResultType.Should().Be(ResultType.Failure);
        responseFirst.ErrorCode.Should().Be(MergeErrorCodes.UnknownPayload);
    }

    [TestCaseSource(nameof(GetPayloadWithdrawalsTestCases))]
    public virtual async Task
        getPayloadBodiesByHashV1_should_return_payload_bodies_in_order_of_request_block_hashes_and_null_for_unknown_hashes(
            Withdrawal[] withdrawals)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Shanghai.Instance);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        ExecutionPayload executionPayload1 = await SendNewBlockV2(rpc, chain, withdrawals);
        Transaction[] txs = BuildTransactions(
            chain, executionPayload1.BlockHash, TestItem.PrivateKeyA, TestItem.AddressB, 3, 0, out _, out _);

        chain.AddTransactions(txs);

        ExecutionPayload executionPayload2 = await BuildAndSendNewBlockV2(rpc, chain, true, withdrawals);
        Hash256[] blockHashes = new Hash256[]
        {
            executionPayload1.BlockHash, TestItem.KeccakA, executionPayload2.BlockHash
        };
        IEnumerable<ExecutionPayloadBodyV1Result?> payloadBodies =
            rpc.engine_getPayloadBodiesByHashV1(blockHashes).Result.Data;
        ExecutionPayloadBodyV1Result?[] expected = {
            new(Array.Empty<Transaction>(), withdrawals), null, new(txs, withdrawals)
        };

        payloadBodies.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
    }

    [Test]
    public virtual async Task TestStatelessExecution()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance);
        IEngineRpcModule rpc = CreateEngineModule(chain);

        // block 1 - empty
        ExecutionPayload executionPayload1 = await BuildAndSendNewBlockV2(rpc, chain, true, Array.Empty<Withdrawal>());
        Hash256 newHead = executionPayload1!.BlockHash;
        ForkchoiceStateV1 forkchoiceStateV1 = new(newHead, newHead, newHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult = await rpc.engine_forkchoiceUpdatedV2(forkchoiceStateV1);
        fcuResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);


        // block 2
        Transaction[] txs = BuildTransactions(
            chain, executionPayload1.BlockHash, TestItem.PrivateKeyA, TestItem.AddressB, 4, 1, out _, out _);
        chain.AddTransactions(txs);
        ExecutionPayload executionPayload2 = await BuildAndSendNewBlockV2(rpc, chain, true, Array.Empty<Withdrawal>());
        newHead = executionPayload2!.BlockHash;
        forkchoiceStateV1 = new(newHead, newHead, newHead);
        fcuResult = await rpc.engine_forkchoiceUpdatedV2(forkchoiceStateV1);
        fcuResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);


        // block 3
        txs = BuildTransactions(
            chain, executionPayload2.BlockHash, TestItem.PrivateKeyB, TestItem.AddressC, 3, 1, out _, out _);
        chain.AddTransactions(txs);
        ExecutionPayload executionPayload3 = await BuildAndSendNewBlockV2(rpc, chain, true, Array.Empty<Withdrawal>());
        newHead = executionPayload3!.BlockHash;
        forkchoiceStateV1 = new(newHead, newHead, newHead);
        fcuResult = await rpc.engine_forkchoiceUpdatedV2(forkchoiceStateV1);
        fcuResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);


        // block 4
        txs = BuildTransactions(
            chain, executionPayload3.BlockHash, TestItem.PrivateKeyA, TestItem.AddressB, 3, 0, out _, out _);
        chain.AddTransactions(txs);

        ExecutionPayload executionPayload4 = await BuildAndSendNewBlockV2(rpc, chain, true, Array.Empty<Withdrawal>());
        newHead = executionPayload4!.BlockHash;
        forkchoiceStateV1 = new(newHead, newHead, newHead);
        fcuResult = await rpc.engine_forkchoiceUpdatedV2(forkchoiceStateV1);
        fcuResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);

        // block 5
        txs = BuildTransactions(
            chain, executionPayload4.BlockHash, TestItem.PrivateKeyA, TestItem.AddressB, 3, 0, out _, out _);
        chain.AddTransactions(txs);
        ExecutionPayload executionPayload5 = await BuildAndSendNewBlockV2(rpc, chain, true, Array.Empty<Withdrawal>());
        newHead = executionPayload5!.BlockHash;
        forkchoiceStateV1 = new(newHead, newHead, newHead);
        fcuResult = await rpc.engine_forkchoiceUpdatedV2(forkchoiceStateV1);
        fcuResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);

        // block 6
        ExecutionPayload executionPayload6 = await BuildAndSendNewBlockV2(rpc, chain, true, Array.Empty<Withdrawal>());
        newHead = executionPayload6!.BlockHash;
        forkchoiceStateV1 = new ForkchoiceStateV1(newHead, newHead, newHead);
        fcuResult = await rpc.engine_forkchoiceUpdatedV2(forkchoiceStateV1);
        fcuResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);

        // IEnumerable<ExecutionPayloadBodyV1Result?> payloadBodies =
        //     rpc.engine_getPayloadBodiesByRangeV1(1, 6).Result.Data;
        // ExecutionPayloadBodyV1Result?[] expected = [new(txs, Array.Empty<Withdrawal>())];
        //
        // payloadBodies.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());

        using MergeTestBlockchain chain2 = await CreateStatelessBlockchain(Prague.Instance);
        IEngineRpcModule rpc2 = CreateEngineModule(chain2, statelessProcessingEnabled: true);

        Block block1 = chain.BlockTree.FindBlock(1)!;
        await chain2.BlockTree.SuggestBlockAsync(block1, BlockTreeSuggestOptions.ForceSetAsMain);

        Block block2 = chain.BlockTree.FindBlock(2)!;
        await chain2.BlockTree.SuggestBlockAsync(block2, BlockTreeSuggestOptions.ForceSetAsMain);

        Block block3 = chain.BlockTree.FindBlock(3)!;
        await chain2.BlockTree.SuggestBlockAsync(block3, BlockTreeSuggestOptions.ForceSetAsMain);

        Block block4 = chain.BlockTree.FindBlock(4)!;
        await chain2.BlockTree.SuggestBlockAsync(block4, BlockTreeSuggestOptions.ForceSetAsMain);

        Block block5 = chain.BlockTree.FindBlock(5)!;
        await chain2.BlockTree.SuggestBlockAsync(block5, BlockTreeSuggestOptions.ForceSetAsMain);

        Block block6 = chain.BlockTree.FindBlock(6)!;
        await chain2.BlockTree.SuggestBlockAsync(block6, BlockTreeSuggestOptions.ForceSetAsMain);

        ResultWrapper<PayloadStatusV1> executePayloadResult =
            await rpc2.engine_newPayloadV2(executionPayload1);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        executePayloadResult =
            await rpc2.engine_newPayloadV2(executionPayload2);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        executePayloadResult =
            await rpc2.engine_newPayloadV2(executionPayload3);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        executePayloadResult =
            await rpc2.engine_newPayloadV2(executionPayload4);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        executePayloadResult =
            await rpc2.engine_newPayloadV2(executionPayload5);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        executePayloadResult =
            await rpc2.engine_newPayloadV2(executionPayload6);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);
    }

    [Test]
    public virtual async Task TestGethTestnet()
    {
        BlockDecoder decoder = new();

        string block0String =
            "f9021ff90219a00000000000000000000000000000000000000000000000000000000000000000a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a03b8c134d342aa849e6d7c54d2f348ddff8afab4e1b929a4b97d0db0a3433cbf0a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000083020000808347e7c4808080a00000000000000000000000000000000000000000000000000000000000000000880000000000000000843b9aca00a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421c0c0c0";
        string block1String =
            "f90353f90218a0b6972519182a58359ea082793c0f2f8bf13516ad7b247fa86b31666b9978872ba01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a02b9e086dad8fa5f094d05377540c910bd200ebc2ddde9cbdf556edf01fbe571ea05bc6adb39ea9aa931ff304798334ca7079af7338ae6c0aa5fa309acb3d0a5b15a0251f2cb798e965c5d9b11c882f37c69fd2c42b314fabe64d2b4998c76eb93ae8b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000080018347e7c482f6180a80a0000000000000000000000000000000000000000000000000000000000000000088000000000000000084342770c0a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421f90133f8658084342770c08252089400020300000000000000000000000000000000008203e78026a012b305f880108f997fba308aed436c3f9579cc3cd974fbba42d4dbed5eb5bf28a04bfb28644ea72bd4f6d668fbbed3a0352c3318cee582eec7933dbd06380eaa9ff8650184342770c08252089400000000000000000000000000000000000000008203e78025a035084f770e2443be85a5c774532dbacda1f5d0161876cec312e08008833f0a52a028c346755226a6f34f8c83dd9634ce232f71930ed3d56beb660877ee1400244bf8630284342770c0825208940000000000000000000000000000000000000000808026a0eb6949fa8562003ad7492e200b7b73a0c68de8c84998683863c87335d7f7ad28a054f2f110f801036824d6952471d3e3959dd58e639d9169b26632ea1bb814cfecc0c0";
        string block2String =
            "f90b57f90219a08e7e37a7664e1d835638e774cc86cf76c10c205057761512066ca88155e6d8aca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a05832f8c71d85786b0d9120398af8c0f7127492ace63e9abe21560718ddef172da002010f59e092529d1f2b457e7c1911a08cdf728a14ac1088ea9615dce3b87447a087acc91f5561bb4a9b68e34b0399003c74a3a428dad7d1151f8aa055c9141f93b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000080028347e7c483041a6f1480a00000000000000000000000000000000000000000000000000000000000000000880000000000000000842dcf2261a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421f90936f8650384342770c08252089401020300000000000000000000000000000000008203e78026a0b1dc957ad929efeadb591bd77121aea2a1808c8a82e460d671aaccce2e96855aa038de06965d668cfaeb7d33d97e3c8e03380aac057741afa265756b32d07c43b0f8650484342770c08252089400000000000000000000000000000000000000008203e78025a013c16fdd3018f4cdd2db8cd0cdf694973de65df7179bbb0c10de42583acaed7aa0633d469034029959d960c23d3b028ae68360c1a4a66663f4d841aaca69b0c6ebf8630584342770c0825208940000000000000000000000000000000000000000808025a0640b2c69c75bedf0b9388e5b60e40ea6f4ea0a084b6660ab27ace0c66dfc04c7a052dc3febc7ca404f37b267a961de42ea35e6de362fcefc6f6928a830837f0ce5f86a0684342770c0832dc6c080109a6060604052600a8060106000396000f360606040526008565b0026a0e909f28a02715713732d38899d8dfe97688ffa3dc7a96a5072b367bac35badcba061e24f56eab4f791158b16ca771b7914d85d401f549618329624be3d546adef9f907940784342770c0832dc6c08080b9074260806040526040516100109061017b565b604051809103906000f08015801561002c573d6000803e3d6000fd5b506000806101000a81548173ffffffffffffffffffffffffffffffffffffffff021916908373ffffffffffffffffffffffffffffffffffffffff16021790555034801561007857600080fd5b5060008067ffffffffffffffff8111156100955761009461024a565b5b6040519080825280601f01601f1916602001820160405280156100c75781602001600182028036833780820191505090505b50905060008060009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1690506020600083833c81610101906101e3565b60405161010d90610187565b61011791906101a3565b604051809103906000f080158015610133573d6000803e3d6000fd5b50600160006101000a81548173ffffffffffffffffffffffffffffffffffffffff021916908373ffffffffffffffffffffffffffffffffffffffff160217905550505061029b565b60d58061046783390190565b6102068061053c83390190565b61019d816101d9565b82525050565b60006020820190506101b86000830184610194565b92915050565b6000819050602082019050919050565b600081519050919050565b6000819050919050565b60006101ee826101ce565b826101f8846101be565b905061020381610279565b925060208210156102435761023e7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff8360200360080261028e565b831692505b5050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052604160045260246000fd5b600061028582516101d9565b80915050919050565b600082821b905092915050565b6101bd806102aa6000396000f3fe608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f566852414610030575b600080fd5b61003861004e565b6040516100459190610146565b60405180910390f35b6000600160009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff166381ca91d36040518163ffffffff1660e01b815260040160206040518083038186803b1580156100b857600080fd5b505afa1580156100cc573d6000803e3d6000fd5b505050506040513d601f19601f820116820180604052508101906100f0919061010a565b905090565b60008151905061010481610170565b92915050565b6000602082840312156101205761011f61016b565b5b600061012e848285016100f5565b91505092915050565b61014081610161565b82525050565b600060208201905061015b6000830184610137565b92915050565b6000819050919050565b600080fd5b61017981610161565b811461018457600080fd5b5056fea2646970667358221220a6a0e11af79f176f9c421b7b12f441356b25f6489b83d38cc828a701720b41f164736f6c63430008070033608060405234801561001057600080fd5b5060b68061001f6000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c8063ab5ed15014602d575b600080fd5b60336047565b604051603e9190605d565b60405180910390f35b60006001905090565b6057816076565b82525050565b6000602082019050607060008301846050565b92915050565b600081905091905056fea26469706673582212203a14eb0d5cd07c277d3e24912f110ddda3e553245a99afc4eeefb2fbae5327aa64736f6c63430008070033608060405234801561001057600080fd5b5060405161020638038061020683398181016040528101906100329190610063565b60018160001c6100429190610090565b60008190555050610145565b60008151905061005d8161012e565b92915050565b60006020828403121561007957610078610129565b5b60006100878482850161004e565b91505092915050565b600061009b826100f0565b91506100a6836100f0565b9250827fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff038211156100db576100da6100fa565b5b828201905092915050565b6000819050919050565b6000819050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fd5b600080fd5b610137816100e6565b811461014257600080fd5b50565b60b3806101536000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c806381ca91d314602d575b600080fd5b60336047565b604051603e9190605a565b60405180910390f35b60005481565b6054816073565b82525050565b6000602082019050606d6000830184604d565b92915050565b600081905091905056fea26469706673582212209bff7098a2f526de1ad499866f27d6d0d6f17b74a413036d6063ca6a0998ca4264736f6c6343000807003326a0e910089d33abf2c4fbc11ae870a94928bceb63362c2df12d88769e40132c69aba04c148c16c0b06a51ccf2f9644552a4510cce5dda2626c1912b0ddb8a738020a2c0c0";

        Block block0 = decoder.Decode(new RlpStream(Bytes.FromHexString(block0String)))!;
        Block block1 = decoder.Decode(new RlpStream(Bytes.FromHexString(block1String)))!;
        Block block2 = decoder.Decode(new RlpStream(Bytes.FromHexString(block2String)))!;

        var ex1 = new ExecutionPayload(block1);
        var ex2 = new ExecutionPayload(block2);

        Dictionary<Address, ChainSpecAllocation> genesisAllocation = new();
        genesisAllocation[new Address(Bytes.FromHexString("0x71562b71999873DB5b286dF957af199Ec94617F7"))] =
            new ChainSpecAllocation(1.Ether());

        MergeTestBlockchain baseChain = CreateBaseBlockchain(null, null);
        baseChain.GenesisBlockBuilder = Build.A.Block.Genesis.Genesis.WithTimestamp(0UL)
            .WithDifficulty(block0.Difficulty).WithBaseFeePerGas(block0.BaseFeePerGas)
            .WithExtraData(Array.Empty<byte>()).WithGasLimit(block0.GasLimit)
            .WithWithdrawalsRoot(block0.WithdrawalsRoot).WithNonce(block0.Nonce);
        using MergeTestBlockchain chain = await baseChain.Build(new TestSingleReleaseSpecProvider(Prague.Instance),
            addBlockOnStart: false, genesisAllocation: genesisAllocation);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 head = chain.BlockTree.HeadHash;
        ResultWrapper<PayloadStatusV1> executePayloadResult =
            await rpc.engine_newPayloadV2(ex1);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);
        Console.WriteLine(head);
        executePayloadResult =
            await rpc.engine_newPayloadV2(ex2);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);
        Console.WriteLine(head);
    }

    [Test]
    public async Task getPayloadBodiesByRangeV1_empty_response()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IEnumerable<ExecutionPayloadBodyV1Result?> payloadBodies =
            rpc.engine_getPayloadBodiesByRangeV1(1, 1).Result.Data;
        ExecutionPayloadBodyV1Result?[] expected = Array.Empty<ExecutionPayloadBodyV1Result?>();

        payloadBodies.Should().BeEquivalentTo(expected);
    }

    [Test]
    public async Task getPayloadBodiesByRangeV1_should_fail_when_too_many_payloads_requested()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>> result =
            rpc.engine_getPayloadBodiesByRangeV1(1, 1025);

        result.Result.ErrorCode.Should().Be(MergeErrorCodes.TooLargeRequest);
    }

    [Test]
    public async Task getPayloadBodiesByHashV1_should_fail_when_too_many_payloads_requested()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256[] hashes = Enumerable.Repeat(TestItem.KeccakA, 1025).ToArray();
        Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>> result =
            rpc.engine_getPayloadBodiesByHashV1(hashes);

        result.Result.ErrorCode.Should().Be(MergeErrorCodes.TooLargeRequest);
    }

    [Test]
    public async Task getPayloadBodiesByRangeV1_should_fail_when_params_below_1()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>> result =
            rpc.engine_getPayloadBodiesByRangeV1(0, 1);

        result.Result.ErrorCode.Should().Be(ErrorCodes.InvalidParams);

        result = await rpc.engine_getPayloadBodiesByRangeV1(1, 0);

        result.Result.ErrorCode.Should().Be(ErrorCodes.InvalidParams);
    }

    [TestCaseSource(nameof(GetPayloadWithdrawalsTestCases))]
    public virtual async Task getPayloadBodiesByRangeV1_should_return_canonical(Withdrawal[] withdrawals)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Shanghai.Instance);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        ExecutionPayload executionPayload1 = await SendNewBlockV2(rpc, chain, withdrawals);

        await rpc.engine_forkchoiceUpdatedV2(new ForkchoiceStateV1(executionPayload1.BlockHash!,
            executionPayload1.BlockHash!, executionPayload1.BlockHash!));

        Block head = chain.BlockTree.Head!;

        // First branch
        {
            Transaction[] txsA = BuildTransactions(
                chain, executionPayload1.BlockHash!, TestItem.PrivateKeyA, TestItem.AddressA, 1, 0, out _, out _);

            chain.AddTransactions(txsA);

            ExecutionPayload executionPayload2 = await BuildAndGetPayloadResultV2(
                rpc, chain, head.Hash!, head.Hash!, head.Hash!, 1001, Keccak.Zero, Address.Zero, withdrawals);
            ResultWrapper<PayloadStatusV1> execResult = await rpc.engine_newPayloadV2(executionPayload2);

            execResult.Data.Status.Should().Be(PayloadStatus.Valid);

            ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult = await rpc.engine_forkchoiceUpdatedV2(
                new ForkchoiceStateV1(executionPayload2.BlockHash!, head.Hash!, head.Hash!));

            fcuResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);

            IEnumerable<ExecutionPayloadBodyV1Result?> payloadBodies =
                rpc.engine_getPayloadBodiesByRangeV1(1, 3).Result.Data;
            ExecutionPayloadBodyV1Result[] expected =
            {
                new(Array.Empty<Transaction>(), withdrawals), new(txsA, withdrawals)
            };

            payloadBodies.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
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

            ResultWrapper<PayloadStatusV1> fcuResult = await rpc.engine_newPayloadV2(new ExecutionPayload(newBlock));

            fcuResult.Data.Status.Should().Be(PayloadStatus.Valid);

            await rpc.engine_forkchoiceUpdatedV2(
                new ForkchoiceStateV1(newBlock.Hash!, newBlock.Hash!, newBlock.Hash!));

            IEnumerable<ExecutionPayloadBodyV1Result?> payloadBodies =
                rpc.engine_getPayloadBodiesByRangeV1(1, 3).Result.Data;
            ExecutionPayloadBodyV1Result[] expected =
            {
                new(Array.Empty<Transaction>(), withdrawals), new(Array.Empty<Transaction>(), withdrawals)
            };

            payloadBodies.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
        }
    }

    [TestCaseSource(nameof(PayloadBodiesByRangeNullTrimTestCases))]
    public async Task getPayloadBodiesByRangeV1_should_trim_trailing_null_bodies(
        (Func<CallInfo, Block?> Impl,
            IEnumerable<ExecutionPayloadBodyV1Result?> Outcome) input)
    {
        IBlockTree? blockTree = Substitute.For<IBlockTree>();

        blockTree.Head.Returns(Build.A.Block.WithNumber(5).TestObject);
        blockTree.FindBlock(Arg.Any<long>()).Returns(input.Impl);

        using MergeTestBlockchain chain = await CreateBlockchain(Shanghai.Instance);
        chain.BlockTree = blockTree;

        IEngineRpcModule rpc = CreateEngineModule(chain);
        IEnumerable<ExecutionPayloadBodyV1Result?> payloadBodies =
            rpc.engine_getPayloadBodiesByRangeV1(1, 5).Result.Data;

        payloadBodies.Should().BeEquivalentTo(input.Outcome);
    }

    [Test]
    public async Task getPayloadBodiesByRangeV1_should_return_up_to_best_body_number()
    {
        IBlockTree? blockTree = Substitute.For<IBlockTree>();

        blockTree.FindBlock(Arg.Any<long>())
            .Returns(i => Build.A.Block.WithNumber(i.ArgAt<long>(0)).TestObject);
        blockTree.Head.Returns(Build.A.Block.WithNumber(5).TestObject);

        using MergeTestBlockchain chain = await CreateBlockchain(Shanghai.Instance);
        chain.BlockTree = blockTree;

        IEngineRpcModule rpc = CreateEngineModule(chain);
        IEnumerable<ExecutionPayloadBodyV1Result?> payloadBodies =
            rpc.engine_getPayloadBodiesByRangeV1(1, 7).Result.Data;

        payloadBodies.Count().Should().Be(5);
    }

    [Test]
    public virtual async Task newPayloadV1_should_fail_with_withdrawals()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(null, new MergeConfig { TerminalTotalDifficulty = "0" });
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
            Withdrawals = Array.Empty<Withdrawal>()
        };

        string response = await RpcTest.TestSerializedRequest(rpcModule, "engine_newPayloadV1",
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
        Withdrawal[]? Withdrawals,
        string BlockHash
        ) input)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(input.Spec);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
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
            Transactions = Array.Empty<byte[]>(),
            Withdrawals = input.Withdrawals
        };

        string response = await RpcTest.TestSerializedRequest(rpcModule, "engine_newPayloadV2",
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
        Withdrawal[]? Withdrawals,
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
            Array.Empty<Withdrawal>(),
            "0xaa4aa15951a28e6adab430a795e36a84649bbafb1257eda23e38b9131cbd3b98");
    }

    [TestCaseSource(nameof(ZeroWithdrawalsTestCases))]
    public async Task executePayloadV2_works_correctly_when_0_withdrawals_applied((
        IReleaseSpec ReleaseSpec,
        Withdrawal[]? Withdrawals,
        bool IsValid) input)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(input.ReleaseSpec);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        ExecutionPayload executionPayload = CreateBlockRequest(chain,
            CreateParentBlockRequestOnHead(chain.BlockTree),
            TestItem.AddressD, input.Withdrawals);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV2(executionPayload);

        if (input.IsValid)
            resultWrapper.Data.Status.Should().Be(PayloadStatus.Valid);
        else
            resultWrapper.ErrorCode.Should().Be(ErrorCodes.InvalidParams);
    }

    [TestCaseSource(nameof(WithdrawalsTestCases))]
    public virtual async Task Can_apply_withdrawals_correctly(
        (Withdrawal[][] Withdrawals, (Address Account, UInt256 BalanceIncrease)[] ExpectedAccountIncrease) input)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Shanghai.Instance);
        IEngineRpcModule rpc = CreateEngineModule(chain);

        // get initial balances
        List<UInt256> initialBalances = new();
        foreach ((Address Account, UInt256 BalanceIncrease) accountIncrease in input.ExpectedAccountIncrease)
        {
            UInt256 initialBalance =
                chain.StateReader.GetBalance(chain.BlockTree.Head!.StateRoot!, accountIncrease.Account);
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
            resultWrapper.Data.Status.Should().Be(PayloadStatus.Valid);
            ResultWrapper<ForkchoiceUpdatedV1Result> resultFcu = await rpc.engine_forkchoiceUpdatedV2(
                new(payload.BlockHash, payload.BlockHash, payload.BlockHash));
            resultFcu.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
        }

        // check balance increase
        for (int index = 0; index < input.ExpectedAccountIncrease.Length; index++)
        {
            (Address Account, UInt256 BalanceIncrease) accountIncrease = input.ExpectedAccountIncrease[index];
            UInt256 currentBalance =
                chain.StateReader.GetBalance(chain.BlockTree.Head!.StateRoot!, accountIncrease.Account);
            currentBalance.Should().Be(accountIncrease.BalanceIncrease + initialBalances[index]);
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
        IEngineRpcModule rpc = CreateEngineModule(chain);

        // Block without withdrawals, Timestamp = 2
        ExecutionPayload executionPayload =
            CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);
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
        ExecutionPayload payloadWithWithdrawals =
            (await BuildAndGetPayloadResultV2(rpc, chain, payloadAttributes))?.ExecutionPayload!;
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
            $"PayloadAttributes {{Timestamp: {attrs.Timestamp}, PrevRandao: {attrs.PrevRandao}, SuggestedFeeRecipient: {attrs.SuggestedFeeRecipient}, Withdrawals count: {attrs.Withdrawals.Length}}}");
    }

    [TestCaseSource(nameof(PayloadIdTestCases))]
    public void Should_compute_payload_id_with_withdrawals((Withdrawal[]? Withdrawals, string PayloadId) input)
    {
        var blockHeader = Build.A.BlockHeader.TestObject;
        var payloadAttributes = new PayloadAttributes
        {
            PrevRandao = Keccak.Zero,
            SuggestedFeeRecipient = Address.Zero,
            Timestamp = 0,
            Withdrawals = input.Withdrawals
        };

        var payloadId = payloadAttributes.GetPayloadId(blockHeader);

        payloadId.Should().Be(input.PayloadId);
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

    protected static IEnumerable<(
        Withdrawal[][] Withdrawals, // withdrawals per payload
        (Address, UInt256)[] expectedAccountIncrease)> WithdrawalsTestCases()
    {
        yield return (new[] { Array.Empty<Withdrawal>() }, Array.Empty<(Address, UInt256)>());
        yield return (new[] { new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalB_2Eth } },
            new[] { (TestItem.AddressA, 1.Ether()), (TestItem.AddressB, 2.Ether()) });
        yield return (new[] { new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalA_1Eth } },
            new[] { (TestItem.AddressA, 2.Ether()), (TestItem.AddressB, 0.Ether()) });
        yield return (
            new[]
            {
                new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalA_1Eth }, new[] { TestItem.WithdrawalA_1Eth }
            }, new[] { (TestItem.AddressA, 3.Ether()), (TestItem.AddressB, 0.Ether()) });
        yield return (new[]
            {
                new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalA_1Eth }, // 1st payload
                new[] { TestItem.WithdrawalA_1Eth }, // 2nd payload
                Array.Empty<Withdrawal>(), // 3rd payload
                new[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalC_3Eth }, // 4th payload
                new[] { TestItem.WithdrawalB_2Eth, TestItem.WithdrawalF_6Eth }, // 5th payload
            },
            new[]
            {
                (TestItem.AddressA, 4.Ether()), (TestItem.AddressB, 2.Ether()), (TestItem.AddressC, 3.Ether()),
                (TestItem.AddressF, 6.Ether())
            });
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
        SemaphoreSlim blockImprovementLock = new SemaphoreSlim(0);

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
            Withdrawals = withdrawals
        };
        string? payloadId = rpc.engine_forkchoiceUpdatedV2(forkchoiceState, payloadAttributes).Result.Data.PayloadId;

        if (waitForBlockImprovement)
            await blockImprovementLock.WaitAsync(10000);

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
        ulong timestamp = chain.BlockTree.Head!.Timestamp + 12;
        Hash256 random = Keccak.Zero;
        Address feeRecipient = Address.Zero;
        ExecutionPayload executionPayload = await BuildAndGetPayloadResultV2(rpc, chain, head,
            Keccak.Zero, head, timestamp, random, feeRecipient, withdrawals, waitForBlockImprovement);
        ResultWrapper<PayloadStatusV1> executePayloadResult =
            await rpc.engine_newPayloadV2(executionPayload);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);
        return executionPayload;
    }

    private async Task<ExecutionPayload> SendNewBlockV2(IEngineRpcModule rpc, MergeTestBlockchain chain,
        Withdrawal[]? withdrawals)
    {
        ExecutionPayload executionPayload = CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals);
        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV2(executionPayload);

        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        return executionPayload;
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

    protected static IEnumerable<(
        Func<CallInfo, Block?>,
        IEnumerable<ExecutionPayloadBodyV1Result?>
        )> PayloadBodiesByRangeNullTrimTestCases()
    {
        Block block = Build.A.Block.TestObject;
        ExecutionPayloadBodyV1Result result = new ExecutionPayloadBodyV1Result(Array.Empty<Transaction>(), null);

        yield return (
            new Func<CallInfo, Block?>(i => null),
            new ExecutionPayloadBodyV1Result?[] { null, null, null, null, null }
        );

        yield return (
            new Func<CallInfo, Block?>(i => i.ArgAt<long>(0) % 2 == 0 ? block : null),
            new[] { null, result, null, result, null }
        );

        yield return (
            new Func<CallInfo, Block?>(i => block),
            Enumerable.Repeat(result, 5)
        );
    }

    protected static IEnumerable<(
        Withdrawal[]? Withdrawals,
        string payloadId
        )> PayloadIdTestCases()
    {
        yield return (null, "0xe3b6f7433feedc38");
        yield return (Array.Empty<Withdrawal>(), "0xf74921b673b2e08e");
        yield return (new[] { Build.A.Withdrawal.TestObject }, "0xe0d0b996245ec3a6");
    }
}
