// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Data.V2;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
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
    public async Task getPayloadV2_received_fees_should_be_equal_to_block_value_in_getPayload_result()
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

        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult.ExecutionPayloadV1);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        UInt256 finalBalance = chain.StateReader.GetBalance(getPayloadResult.ExecutionPayloadV1.StateRoot, feeRecipient);

        (finalBalance - startingBalance).Should().Be(getPayloadResult.BlockValue);
    }

    [Test]
    public async Task getPayloadV2_request_unknown_payload()
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
}
