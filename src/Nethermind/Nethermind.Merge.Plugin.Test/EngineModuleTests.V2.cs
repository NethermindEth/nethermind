// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public virtual async Task V2_processing_block_should_serialize_valid_responses()
    {
        using MergeTestBlockchain chain = await CreateShanghaiBlockChain(new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        Keccak prevRandao = Keccak.Zero;
        Address feeRecipient = TestItem.AddressC;
        UInt256 timestamp = Timestamper.UnixTime.Seconds;


        var forkChoiceUpdatedParams = new { headBlockHash = startingHead.ToString(), safeBlockHash = startingHead.ToString(), finalizedBlockHash = Keccak.Zero.ToString(), };
        var withdrawals = new Withdrawal[] { new Withdrawal() { Index = 1, Amount = 3, Address = TestItem.AddressB, ValidatorIndex = 2 } };
        var preparePayloadParams = new { timestamp = timestamp.ToHexString(true), prevRandao = prevRandao.ToString(), suggestedFeeRecipient = feeRecipient.ToString(), withdrawals = withdrawals };
        string?[] parameters = { JsonConvert.SerializeObject(forkChoiceUpdatedParams), JsonConvert.SerializeObject(preparePayloadParams) };
        // prepare a payload
        string result = RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV2", parameters);
        byte[] expectedPayloadId = Bytes.FromHexString("0x6454408c425ddd96");
        result.Should()
            .Be(
                $"{{\"jsonrpc\":\"2.0\",\"result\":{{\"payloadStatus\":{{\"status\":\"VALID\",\"latestValidHash\":\"0x1c53bdbf457025f80c6971a9cf50986974eed02f0a9acaeeb49cafef10efd133\",\"validationError\":null}},\"payloadId\":\"{expectedPayloadId.ToHexString(true)}\"}},\"id\":67}}");

        Keccak blockHash = new("0x6817d4b48be0bc14f144cc242cdc47a5ccc40de34b9c3934acad45057369f576");
        var expectedPayload = chain.JsonSerializer.Serialize(new ExecutionPayload
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
            Timestamp = timestamp.ToUInt64(null),
            Transactions = Array.Empty<byte[]>(),
            Withdrawals = withdrawals
        });
        // get the payload
        result = RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV2", expectedPayloadId.ToHexString(true));
        result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedPayload},\"id\":67}}");
        // execute the payload
        result = RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV2", expectedPayload);
        result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"status\":\"VALID\",\"latestValidHash\":\"{blockHash}\",\"validationError\":null}},\"id\":67}}");

        forkChoiceUpdatedParams = new { headBlockHash = blockHash.ToString(true), safeBlockHash = blockHash.ToString(true), finalizedBlockHash = startingHead.ToString(true), };
        parameters = new[] { JsonConvert.SerializeObject(forkChoiceUpdatedParams), null };
        // update the fork choice
        result = RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV2", parameters);
        result.Should().Be("{\"jsonrpc\":\"2.0\",\"result\":{\"payloadStatus\":{\"status\":\"VALID\",\"latestValidHash\":\"" +
                           blockHash +
                           "\",\"validationError\":null},\"payloadId\":null},\"id\":67}");
    }
}
