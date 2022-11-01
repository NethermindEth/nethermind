using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
        [Test]
        public virtual async Task V2_processing_block_should_serialize_valid_responses()
        {
            using MergeTestBlockchain chain = await CreateBlockChain(new MergeConfig()
            {
                TerminalTotalDifficulty = "0"
            });
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            Keccak prevRandao = Keccak.Zero;
            Address feeRecipient = TestItem.AddressC;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;


            var forkChoiceUpdatedParams = new
            {
                headBlockHash = startingHead.ToString(),
                safeBlockHash = startingHead.ToString(),
                finalizedBlockHash = Keccak.Zero.ToString(),
            };
            var preparePayloadParams = new
            {
                timestamp = timestamp.ToHexString(true),
                prevRandao = prevRandao.ToString(),
                suggestedFeeRecipient = feeRecipient.ToString(),
            };
            string?[] parameters =
            {
                JsonConvert.SerializeObject(forkChoiceUpdatedParams),
                JsonConvert.SerializeObject(preparePayloadParams)
            };
            // prepare a payload
            string result = RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV2", parameters);
            byte[] expectedPayloadId = Bytes.FromHexString("0x6454408c425ddd96");
            result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"payloadStatus\":{{\"status\":\"VALID\",\"latestValidHash\":\"0x1c53bdbf457025f80c6971a9cf50986974eed02f0a9acaeeb49cafef10efd133\",\"validationError\":null}},\"payloadId\":\"{expectedPayloadId.ToHexString(true)}\"}},\"id\":67}}");

            Keccak blockHash = new("0xb1b3b07ef3832bd409a04fdea9bf2bfa83d7af0f537ff25f4a3d2eb632ebfb0f");
            var expectedPayload = new
            {
                parentHash = startingHead.ToString(),
                feeRecipient = feeRecipient.ToString(),
                stateRoot = chain.BlockTree.Head!.StateRoot!.ToString(),
                receiptsRoot = chain.BlockTree.Head!.ReceiptsRoot!.ToString(),
                logsBloom = Bloom.Empty.Bytes.ToHexString(true),
                prevRandao = prevRandao.ToString(),
                blockNumber = "0x1",
                gasLimit = chain.BlockTree.Head!.GasLimit.ToHexString(true),
                gasUsed = "0x0",
                timestamp = timestamp.ToHexString(true),
                extraData = "0x4e65746865726d696e64", // Nethermind
                baseFeePerGas = "0x0",
                blockHash = blockHash.ToString(),
                transactions = Array.Empty<object>(),
            };
            string expectedPayloadString = JsonConvert.SerializeObject(expectedPayload);
            // get the payload
            result = RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV2", expectedPayloadId.ToHexString(true));
            result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedPayloadString},\"id\":67}}");
            // execute the payload
            result = RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV2", expectedPayloadString);
            result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"status\":\"VALID\",\"latestValidHash\":\"{blockHash}\",\"validationError\":null}},\"id\":67}}");

            forkChoiceUpdatedParams = new
            {
                headBlockHash = blockHash.ToString(true),
                safeBlockHash = blockHash.ToString(true),
                finalizedBlockHash = startingHead.ToString(true),
            };
            parameters = new[] { JsonConvert.SerializeObject(forkChoiceUpdatedParams), null };
            // update the fork choice
            result = RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV2", parameters);
            result.Should().Be("{\"jsonrpc\":\"2.0\",\"result\":{\"payloadStatus\":{\"status\":\"VALID\",\"latestValidHash\":\"" +
                               blockHash +
                               "\",\"validationError\":null},\"payloadId\":null},\"id\":67}");
        }
}
