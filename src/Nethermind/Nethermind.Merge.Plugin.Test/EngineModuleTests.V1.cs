//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public partial class EngineModuleTests
    {
        [Test]
        public async Task processing_block_should_serialize_valid_responses()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;

            byte[] expectedPayloadId = Bytes.FromHexString("0x45bd36a8143d860c");

            var forkChoiceUpdatedParams = new
            {
                headBlockHash = startingHead.ToString(),
                safeBlockHash = startingHead.ToString(),
                finalizedBlockHash = Keccak.Zero.ToString(),
            };
            var preparePayloadParams = new
            {
                timestamp = timestamp.ToHexString(true),
                random = random.ToString(),
                feeRecipient = feeRecipient.ToString(),
            };
            string?[] parameters =
            {
                JsonConvert.SerializeObject(forkChoiceUpdatedParams),
                JsonConvert.SerializeObject(preparePayloadParams)
            };
            // prepare a payload
            string result = RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV1", parameters);
            result.Should()
                .Be(
                    $"{{\"jsonrpc\":\"2.0\",\"result\":{{\"status\":\"SUCCESS\",\"payloadId\":\"{expectedPayloadId.ToHexString(true)}\"}},\"id\":67}}");

            Keccak blockHash = new Keccak("0xdc4e882186c2723e6ed279634d6b7f502bf2712dc3113c743903786b61c55c87");
            var expectedPayload = new
            {
                parentHash = startingHead.ToString(),
                coinbase = chain.MinerAddress.ToString(),
                stateRoot = chain.BlockTree.Head!.StateRoot!.ToString(),
                receiptRoot = chain.BlockTree.Head!.ReceiptsRoot!.ToString(),
                logsBloom = Bloom.Empty.Bytes.ToHexString(true),
                random = random.ToString(),
                blockNumber = "0x1",
                gasLimit = chain.BlockTree.Head!.GasLimit.ToHexString(true),
                gasUsed = "0x0",
                timestamp = timestamp.ToHexString(true),
                extraData = "0x",
                baseFeePerGas = "0x0",
                blockHash = blockHash.ToString(),
                transactions = Array.Empty<object>(),
            };
            string expectedPayloadString = JsonConvert.SerializeObject(expectedPayload);
            // get the payload
            result = RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV1", expectedPayloadId.ToHexString(true));
            result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedPayloadString},\"id\":67}}");
            // execute the payload
            result = RpcTest.TestSerializedRequest(rpc, "engine_executePayloadV1", expectedPayloadString);
            result.Should()
                .Be(
                    $"{{\"jsonrpc\":\"2.0\",\"result\":{{\"status\":\"VALID\",\"latestValidHash\":\"{blockHash}\"}},\"id\":67}}");

            forkChoiceUpdatedParams = new
            {
                headBlockHash = blockHash.ToString(true),
                safeBlockHash = blockHash.ToString(true),
                finalizedBlockHash = startingHead.ToString(true),
            };
            parameters = new[] {JsonConvert.SerializeObject(forkChoiceUpdatedParams), null};
            // update the fork choice
            result = RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV1", parameters);
            result.Should()
                .Be("{\"jsonrpc\":\"2.0\",\"result\":{\"status\":\"SUCCESS\",\"payloadId\":\"0x\"},\"id\":67}");
        }

        [Test]
        public async Task getPayload_should_serialize_unknown_payload_response_properly()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            byte[] payloadId = Bytes.FromHexString("0x1111111111111111");
            ;

            string parameters = payloadId.ToHexString(true);
            string result = RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV1", parameters);
            result.Should()
                .Be("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"unknown payload\"},\"id\":67}");
        }

        [Test, Retry(3)]
        public async Task engine_forkchoiceUpdatedV1_should_create_block_on_top_of_genesis()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = 30;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;

            BlockRequestResult? blockRequestResult = await BuildAndGetPayloadResult(rpc, startingHead,
                Keccak.Zero, startingHead, timestamp, random, feeRecipient);

            BlockRequestResult expected = CreateParentBlockRequestOnHead(chain.BlockTree);
            expected.GasLimit = 4000000L;
            expected.BlockHash = new Keccak("0x33228284b2c8d36e3fd34c31de3ab0604412bf9ab71725307d13daa2c4f44348");
            expected.LogsBloom = Bloom.Empty;
            expected.Coinbase = chain.MinerAddress;
            expected.BlockNumber = 1;
            expected.Random = random;
            expected.ParentHash = startingHead;
            expected.SetTransactions(Array.Empty<Transaction>());
            expected.Timestamp = timestamp;
            expected.Random = random;
            expected.ExtraData = Array.Empty<byte>();

            blockRequestResult.Should().BeEquivalentTo(expected);
        }

        [Test]
        public async Task getPayloadV1_should_return_error_if_there_was_no_corresponding_preparePayloadV1()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;
            string payloadId = rpc.engine_forkchoiceUpdatedV1(new (startingHead, Keccak.Zero, startingHead),
                new () { Timestamp = timestamp, FeeRecipient = feeRecipient, Random = random}).Result.Data.PayloadId;
        
            byte[] requestedPayloadId = (Convert.ToInt32(payloadId, 16) + 1).ToByteArray();
            ResultWrapper<BlockRequestResult?> response = await rpc.engine_getPayloadV1(requestedPayloadId);
        
            response.ErrorCode.Should().Be(MergeErrorCodes.UnavailablePayload);
        }
        
        private async Task<BlockRequestResult> BuildAndGetPayloadResult(
            IEngineRpcModule rpc, Keccak headBlockHash, Keccak finalizedBlockHash, Keccak safeBlockHash,
            UInt256 timestamp, Keccak random, Address feeRecipient)
        {
            ForkchoiceStateV1 forkchoiceState = new(headBlockHash, finalizedBlockHash, safeBlockHash);
            PayloadAttributes payloadAttributes =
                new() {Timestamp = timestamp, Random = random, FeeRecipient = feeRecipient};
            string payloadId =
                rpc.engine_forkchoiceUpdatedV1(forkchoiceState, payloadAttributes).Result.Data.PayloadId;
            ResultWrapper<BlockRequestResult?> getPayloadResult =
                await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));
            return getPayloadResult.Data!;
        }

        private async Task<BlockRequestResult> BuildAndGetPayloadResult(MergeTestBlockchain chain,
            IEngineRpcModule rpc)
        {
            Keccak startingHead = chain.BlockTree.HeadHash;
            Keccak parentHead = chain.BlockTree.Head!.ParentHash!;

            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;

            return await BuildAndGetPayloadResult(rpc, startingHead, parentHead, startingHead,
                timestamp, random, feeRecipient);
        }
    }
}
