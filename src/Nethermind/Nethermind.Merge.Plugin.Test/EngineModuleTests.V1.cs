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
using System.Collections;
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
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.State;
using Nethermind.Trie;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public partial class EngineModuleTests
    {
        [Test]
        public async Task getPayload_correctlyEncodeTransactions()
        {
            byte[] payload = new byte[0];
            using MergeTestBlockchain chain = await CreateBlockChain();
            IPayloadService payloadService = Substitute.For<IPayloadService>();
            Block block = Build.A.Block.WithTransactions(
                new[]
                {
                    Build.A.Transaction.WithTo(TestItem.AddressD)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                    Build.A.Transaction.WithTo(TestItem.AddressD).WithType(TxType.EIP1559).WithMaxFeePerGas(20)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject
                }).TestObject;
            payloadService.GetPayload(Arg.Any<byte[]>()).Returns(block);
            IEngineRpcModule rpc = CreateEngineModule(chain, payloadService);
            
            string result = RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV1", payload.ToHexString(true));
            Assert.AreEqual(result,
                "{\"jsonrpc\":\"2.0\",\"result\":{\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"feeRecipient\":\"0x0000000000000000000000000000000000000000\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"random\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"blockNumber\":\"0x0\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"timestamp\":\"0xf4240\",\"extraData\":\"0x010203\",\"baseFeePerGas\":\"0x0\",\"blockHash\":\"0x5fd61518405272d77fd6cdc8a824a109d75343e32024ee4f6769408454b1823d\",\"transactions\":[\"0xf85f800182520894475674cb523a0a2736b7f7534390288fce16982c018025a0634db2f18f24d740be29e03dd217eea5757ed7422680429bdd458c582721b6c2a02f0fa83931c9a99d3448a46b922261447d6a41d8a58992b5596089d15d521102\",\"0x02f8620180011482520894475674cb523a0a2736b7f7534390288fce16982c0180c001a0033e85439a128c42f2ba47ca278f1375ef211e61750018ff21bcd9750d1893f2a04ee981fe5261f8853f95c865232ffdab009abcc7858ca051fb624c49744bf18d\"]},\"id\":67}");
        }

        [Test]
        public async Task processing_block_should_serialize_valid_responses()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            Keccak random = Keccak.Zero;
            Address feeRecipient = TestItem.AddressC;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;

            byte[] expectedPayloadId = Bytes.FromHexString("0x6454408c425ddd96");

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
                suggestedFeeRecipient = feeRecipient.ToString(),
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

            Keccak blockHash = new Keccak("0x2de2042d5ab1cf7c89d97f93b1572ddac3c6f77d84b6d44d1d9cec42f76505a7");
            var expectedPayload = new
            {
                parentHash = startingHead.ToString(),
                feeRecipient = feeRecipient.ToString(),
                stateRoot = chain.BlockTree.Head!.StateRoot!.ToString(),
                receiptsRoot = chain.BlockTree.Head!.ReceiptsRoot!.ToString(),
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
                .Be("{\"jsonrpc\":\"2.0\",\"result\":{\"status\":\"SUCCESS\"},\"id\":67}");
        }

        [Test]
        public async Task can_parse_forkchoiceUpdated_with_implicit_null_payloadAttributes()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            var forkChoiceUpdatedParams = new
            {
                headBlockHash = Keccak.Zero.ToString(),
                safeBlockHash = Keccak.Zero.ToString(),
                finalizedBlockHash = Keccak.Zero.ToString(),
            };
            string[] parameters = new[] {JsonConvert.SerializeObject(forkChoiceUpdatedParams)};
            string? result = RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV1", parameters);
            // ToDo wait for final PostMerge sync
            result.Should()
                .Be("{\"jsonrpc\":\"2.0\",\"result\":{\"status\":\"SYNCING\"},\"id\":67}");
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
        public async Task
            engine_forkchoiceUpdatedV1_with_payload_attributes_should_create_block_on_top_of_genesis_and_not_change_head()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            ulong timestamp = 30;
            Keccak random = Keccak.Zero;
            Address feeRecipient = TestItem.AddressD;

            BlockRequestResult? blockRequestResult = await BuildAndGetPayloadResult(rpc, startingHead,
                Keccak.Zero, startingHead, timestamp, random, feeRecipient);

            BlockRequestResult expected = CreateParentBlockRequestOnHead(chain.BlockTree);
            expected.GasLimit = 4000000L;
            expected.BlockHash = new Keccak("0x3ee80ba456bac700bfaf5b2827270406134e2392eb03ec50f6c23de28dd08811");
            expected.LogsBloom = Bloom.Empty;
            expected.FeeRecipient = feeRecipient;
            expected.BlockNumber = 1;
            expected.Random = random;
            expected.ParentHash = startingHead;
            expected.SetTransactions(Array.Empty<Transaction>());
            expected.Timestamp = timestamp;
            expected.Random = random;
            expected.ExtraData = Array.Empty<byte>();

            blockRequestResult.Should().BeEquivalentTo(expected);
            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(expected.BlockHash);
            actualHead.Should().Be(startingHead);
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
            string _ = rpc.engine_forkchoiceUpdatedV1(new(startingHead, Keccak.Zero, startingHead),
                    new() {Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, Random = random}).Result.Data
                .PayloadId;

            byte[] requestedPayloadId = Bytes.FromHexString("0x45bd36a8143d860d");
            ResultWrapper<BlockRequestResult?> response = await rpc.engine_getPayloadV1(requestedPayloadId);

            response.ErrorCode.Should().Be(MergeErrorCodes.UnavailablePayloadV1);
        }

        [Test]
        public async Task getPayloadV1_should_return_error_if_called_after_timeout()
        {
            const int timeout = 25000;

            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;

            string payloadId = rpc.engine_forkchoiceUpdatedV1(new(startingHead, Keccak.Zero, startingHead),
                    new() {Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, Random = random}).Result.Data
                .PayloadId;

            Thread.Sleep(timeout);

            ResultWrapper<BlockRequestResult?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

            response.ErrorCode.Should().Be(MergeErrorCodes.UnavailablePayloadV1);
        }
        
        [Test]
        public async Task getPayloadBodiesV1_should_return_payload_bodies_in_order_of_request_block_hashes_and_skip_unknown_hashes()
        {
            int delay = 100;
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;
            
            BlockRequestResult? blockRequestResult = await BuildAndGetPayloadResult(rpc, startingHead,
                Keccak.Zero, startingHead, timestamp, random, feeRecipient, delay);
            Transaction[] txs =
            {
                Build.A.Transaction
                    .WithGasLimit(GasCostOf.Transaction)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject
            };
            chain.AddTransactions(txs);
            BlockRequestResult? blockRequestResult1 = await BuildAndGetPayloadResult(rpc, startingHead,
                Keccak.Zero, startingHead, timestamp + 1, random, feeRecipient, delay);
            Keccak[] blockHashes =
                new[] {blockRequestResult.BlockHash, TestItem.KeccakA, blockRequestResult1.BlockHash};
            ExecutionPayloadBodyV1Result[] payloadBodies = rpc.engine_getPayloadBodiesV1(blockHashes).Result.Data;
            ExecutionPayloadBodyV1Result[] expected = new[]
            {
                new ExecutionPayloadBodyV1Result(Array.Empty<Transaction>()),
                new ExecutionPayloadBodyV1Result(txs)
            };
            payloadBodies.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
        }
         
        [Test]
        public async Task forkchoiceUpdatedV1_should_not_create_block_or_change_head_with_unknown_parent()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            Keccak notExistingHash = TestItem.KeccakH;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;

            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedV1Response = await rpc.engine_forkchoiceUpdatedV1(
                new(notExistingHash, Keccak.Zero, notExistingHash),
                new() {Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, Random = random});

            forkchoiceUpdatedV1Response.Data.Status.Should().Be("SYNCING"); // ToDo wait for final PostMerge sync
            byte[] payloadId = Bytes.FromHexString("0x5d071947bfcc3e65");
            ResultWrapper<BlockRequestResult?> getResponse = await rpc.engine_getPayloadV1(payloadId);

            getResponse.ErrorCode.Should().Be(MergeErrorCodes.UnavailablePayloadV1);
            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(notExistingHash);
            actualHead.Should().Be(startingHead);
        }

        [Test]
        public async Task executePayloadV1_accepts_previously_assembled_block_multiple_times([Values(1, 3)] int times)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockHeader startingBestSuggestedHeader = chain.BlockTree.BestSuggestedHeader!;
            BlockRequestResult getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
            getPayloadResult.ParentHash.Should().Be(startingHead);


            for (int i = 0; i < times; i++)
            {
                ResultWrapper<ExecutePayloadV1Result> executePayloadResult =
                    await rpc.engine_executePayloadV1(getPayloadResult);
                executePayloadResult.Data.Status.Should().Be(ExecutePayloadStatus.Valid);
            }

            Keccak bestSuggestedHeaderHash = chain.BlockTree.BestSuggestedHeader!.Hash!;
            bestSuggestedHeaderHash.Should().Be(getPayloadResult.BlockHash);
            bestSuggestedHeaderHash.Should().NotBe(startingBestSuggestedHeader!.Hash!);
        }

        [Test]
        public async Task executePayloadV1_accepts_previously_prepared_block_multiple_times([Values(1, 3)] int times)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockHeader startingBestSuggestedHeader = chain.BlockTree.BestSuggestedHeader!;
            BlockRequestResult getPayloadResult = await PrepareAndGetPayloadResultV1(chain, rpc);
            getPayloadResult.ParentHash.Should().Be(startingHead);


            for (int i = 0; i < times; i++)
            {
                ResultWrapper<ExecutePayloadV1Result>? executePayloadResult =
                    await rpc.engine_executePayloadV1(getPayloadResult);
                executePayloadResult.Data.Status.Should().Be(ExecutePayloadStatus.Valid);
            }

            Keccak bestSuggestedHeaderHash = chain.BlockTree.BestSuggestedHeader!.Hash!;
            bestSuggestedHeaderHash.Should().Be(getPayloadResult.BlockHash);
            bestSuggestedHeaderHash.Should().NotBe(startingBestSuggestedHeader!.Hash!);
        }


        private async Task<BlockRequestResult> PrepareAndGetPayloadResultV1(MergeTestBlockchain chain,
            IEngineRpcModule rpc)
        {
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;
            return await PrepareAndGetPayloadResultV1(rpc, startingHead, timestamp, random, feeRecipient);
        }

        private async Task<BlockRequestResult> PrepareAndGetPayloadResultV1(
            IEngineRpcModule rpc, Keccak currentHead, UInt256 timestamp, Keccak random, Address feeRecipient)
        {
            PayloadAttributes? payloadAttributes = new PayloadAttributes()
            {
                Random = random, SuggestedFeeRecipient = feeRecipient, Timestamp = timestamp
            };
            ForkchoiceStateV1? forkchoiceStateV1 = new ForkchoiceStateV1(currentHead, currentHead, currentHead);
            ResultWrapper<ForkchoiceUpdatedV1Result>? forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, payloadAttributes);
            byte[] payloadId = Bytes.FromHexString(forkchoiceUpdatedResult.Data.PayloadId);
            ResultWrapper<BlockRequestResult?> getPayloadResult = await rpc.engine_getPayloadV1(payloadId);
            return getPayloadResult.Data!;
        }

        public static IEnumerable WrongInputTestsV1
        {
            get
            {
                yield return GetNewBlockRequestBadDataTestCase(r => r.BlockHash, TestItem.KeccakA);
                yield return GetNewBlockRequestBadDataTestCase(r => r.ReceiptsRoot, TestItem.KeccakD);
                yield return GetNewBlockRequestBadDataTestCase(r => r.StateRoot, TestItem.KeccakD);

                Bloom bloom = new();
                bloom.Add(new[]
                {
                    Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakG).TestObject
                });
                yield return GetNewBlockRequestBadDataTestCase(r => r.LogsBloom, bloom);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Transactions, new[] {new byte[] {1}});
                yield return GetNewBlockRequestBadDataTestCase(r => r.GasUsed, 1);
            }
        }

        // ToDo wait for final PostMerge sync
        [Test]
        public async Task executePayloadV1_unknown_parentHash_return_syncing()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
            Keccak blockHash = getPayloadResult.BlockHash;
            getPayloadResult.ParentHash = TestItem.KeccakF;
            if (blockHash == getPayloadResult.BlockHash && TryCalculateHash(getPayloadResult, out Keccak? hash))
            {
                getPayloadResult.BlockHash = hash;
            }

            ResultWrapper<ExecutePayloadV1Result>
                executePayloadResult = await rpc.engine_executePayloadV1(getPayloadResult);
            executePayloadResult.Data.Status.Should().Be(ExecutePayloadStatus.Syncing);
        }

        [TestCaseSource(nameof(WrongInputTestsV1))]
        public async Task executePayloadV1_rejects_incorrect_input(Action<BlockRequestResult> breakerAction)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
            Keccak blockHash = getPayloadResult.BlockHash;
            breakerAction(getPayloadResult);
            if (blockHash == getPayloadResult.BlockHash && TryCalculateHash(getPayloadResult, out Keccak? hash))
            {
                getPayloadResult.BlockHash = hash;
            }

            ResultWrapper<ExecutePayloadV1Result>
                executePayloadResult = await rpc.engine_executePayloadV1(getPayloadResult);
            executePayloadResult.Data.Status.Should().Be(ExecutePayloadStatus.Invalid);
        }
        
        [Test]
        public async Task executePayloadV1_accepts_already_known_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Block block = Build.A.Block.WithNumber(1).WithParent(chain.BlockTree.Head!).WithDifficulty(0).WithNonce(0).TestObject;
            block.Header.Hash = block.CalculateHash();
            await chain.BlockTree.SuggestBlockAsync(block!);
            BlockRequestResult blockRequest = new(block);
            ResultWrapper<ExecutePayloadV1Result> executePayloadResult =
                await rpc.engine_executePayloadV1(blockRequest);
            executePayloadResult.Data.Status.Should().Be(ExecutePayloadStatus.Valid);
        }

        [Test]
        public async Task forkchoiceUpdatedV1_should_work_with_zero_keccak_for_finalization()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockRequestResult blockRequestResult = await SendNewBlockV1(rpc, chain);

            Keccak newHeadHash = blockRequestResult.BlockHash;
            ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash!, Keccak.Zero, startingHead);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            forkchoiceUpdatedResult.Data.Status.Should().Be(ForkchoiceStatus.Success);
            forkchoiceUpdatedResult.Data.PayloadId.Should().Be(null);

            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(startingHead);
            actualHead.Should().Be(newHeadHash);
            AssertExecutionStatusChanged(rpc, newHeadHash!, Keccak.Zero, startingHead);
        }

        [Test]
        public async Task forkchoiceUpdatedV1_with_no_payload_attributes_should_change_head()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockRequestResult blockRequestResult = await SendNewBlockV1(rpc, chain);

            Keccak newHeadHash = blockRequestResult.BlockHash;
            ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash!, startingHead, startingHead!);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            forkchoiceUpdatedResult.Data.Status.Should().Be(ForkchoiceStatus.Success);
            forkchoiceUpdatedResult.Data.PayloadId.Should().Be(null);

            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(startingHead);
            actualHead.Should().Be(newHeadHash);
            AssertExecutionStatusChangedV1(rpc, newHeadHash!, startingHead, startingHead);
        }

        [Test]
        public async Task forkChoiceUpdatedV1_to_unknown_block_fails()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            ForkchoiceStateV1 forkchoiceStateV1 =
                new(TestItem.KeccakF, TestItem.KeccakF, TestItem.KeccakF);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            forkchoiceUpdatedResult.Data.Status.Should()
                .Be(nameof(ExecutePayloadStatus.Syncing).ToUpper()); // ToDo wait for final PostMerge sync
            AssertExecutionStatusNotChangedV1(rpc, TestItem.KeccakF, TestItem.KeccakF, TestItem.KeccakF);
        }

        [Test]
        public async Task forkChoiceUpdatedV1_to_unknown_safeBlock_hash_should_fail()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockRequestResult blockRequestResult = await SendNewBlockV1(rpc, chain);

            Keccak newHeadHash = blockRequestResult.BlockHash;
            ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash!, startingHead, TestItem.KeccakF);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            forkchoiceUpdatedResult.Data.Status.Should()
                .Be(nameof(ExecutePayloadStatus.Syncing).ToUpper()); // ToDo wait for final PostMerge sync

            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(newHeadHash);
        }

        [Test]
        public async Task forkChoiceUpdatedV1_no_common_branch_fails()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak? startingHead = chain.BlockTree.HeadHash;
            BlockHeader parent = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            Block block = Build.A.Block.WithNumber(2).WithParent(parent).TestObject;
            chain.BlockTree.SuggestBlock(block);

            ForkchoiceStateV1 forkchoiceStateV1 = new(block.Hash!, startingHead, startingHead);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            forkchoiceUpdatedResult.Data.Status.Should().Be("SYNCING"); // ToDo wait for final PostMerge sync
            AssertExecutionStatusNotChangedV1(rpc, block.Hash!, startingHead, startingHead);
        }

        [Test]
        public async Task forkchoiceUpdatedV1_should_change_head_when_all_parameters_are_the_newHeadHash()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = await SendNewBlockV1(rpc, chain);

            Keccak newHeadHash = blockRequestResult.BlockHash;
            ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash, newHeadHash, newHeadHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            forkchoiceUpdatedResult.Data.Status.Should().Be(ForkchoiceStatus.Success);
            forkchoiceUpdatedResult.Data.PayloadId.Should().Be(null);
            AssertExecutionStatusChangedV1(rpc, newHeadHash, newHeadHash, newHeadHash);
        }

        [Test]
        [Ignore("ToDo - our test setup seems to wrong now. We can't add PoW block")]
        public async Task Can_transition_from_PoW_chain()
        {
            using MergeTestBlockchain chain = await CreateBlockChain(new MergeConfig() { Enabled = true, TerminalTotalDifficulty = "1000001" });
            IEngineRpcModule rpc = CreateEngineModule(chain);
            await chain.AddBlock();
            var head = chain.BlockTree.Head;
        }

        [TestCase("")]
        [TestCase("1000001")]
        public async Task executePayloadV1_should_not_accept_blocks_with_incorrect_ttd(string terminalTotalDifficulty)
        {
            using MergeTestBlockchain chain = await CreateBlockChain(new MergeConfig() { Enabled = true, TerminalTotalDifficulty = terminalTotalDifficulty });
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressD);
            ResultWrapper<ExecutePayloadV1Result> resultWrapper = await rpc.engine_executePayloadV1(blockRequestResult);
            resultWrapper.ErrorCode.Should().Be(MergeErrorCodes.InvalidTerminalBlock);
        }
        
        [TestCase("")]
        [TestCase("1000001")]
        public async Task forkchoiceUpdatedV1_should_not_accept_blocks_with_incorrect_ttd(string terminalTotalDifficulty)
        {
            using MergeTestBlockchain chain = await CreateBlockChain(new MergeConfig() { Enabled = true, TerminalTotalDifficulty = terminalTotalDifficulty });
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak blockHash = chain.BlockTree.HeadHash;
            ResultWrapper<ForkchoiceUpdatedV1Result> resultWrapper = await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(blockHash, blockHash, blockHash), null);
            resultWrapper.ErrorCode.Should().Be(MergeErrorCodes.InvalidTerminalBlock);
        }

        [Test]
        public async Task executePayloadV1_accepts_first_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressD);
            ResultWrapper<ExecutePayloadV1Result> resultWrapper = await rpc.engine_executePayloadV1(blockRequestResult);
            resultWrapper.Data.Status.Should().Be(ExecutePayloadStatus.Valid);
            new BlockRequestResult(chain.BlockTree.BestSuggestedBody).Should()
                .BeEquivalentTo(blockRequestResult);
        }
        
        [Test]
        public async Task executePayloadV1_calculate_hash_for_cached_blocks()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressD);
            ResultWrapper<ExecutePayloadV1Result> resultWrapper = await rpc.engine_executePayloadV1(blockRequestResult);
            resultWrapper.Data.Status.Should().Be(ExecutePayloadStatus.Valid);
            ResultWrapper<ExecutePayloadV1Result> resultWrapper2 = await rpc.engine_executePayloadV1(blockRequestResult);
            resultWrapper2.Data.Status.Should().Be(ExecutePayloadStatus.Valid);
            blockRequestResult.ParentHash = blockRequestResult.BlockHash;
            ResultWrapper<ExecutePayloadV1Result> invalidBlockRequest = await rpc.engine_executePayloadV1(blockRequestResult);
            invalidBlockRequest.Data.Status.Should().Be(ExecutePayloadStatus.Invalid);
        }

        [TestCase(30)]
        public async Task can_progress_chain_one_by_one_v1(int count)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak lastHash = (await ProduceBranchV1(rpc, chain.BlockTree, count, CreateParentBlockRequestOnHead(chain.BlockTree), true))
                .Last()
                .BlockHash;
            chain.BlockTree.HeadHash.Should().Be(lastHash);
            Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, b => b.IsGenesis, true);
            last.Should().NotBeNull();
            last!.IsGenesis.Should().BeTrue();
        }

        [Test]
        public async Task forkchoiceUpdatedV1_can_reorganize_to_any_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);

            async Task CanReorganizeToBlock(BlockRequestResult block, MergeTestBlockchain testChain)
            {
                ForkchoiceStateV1 forkchoiceStateV1 =
                    new(block.BlockHash, block.BlockHash, block.BlockHash);
                ResultWrapper<ForkchoiceUpdatedV1Result> result =
                    await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
                result.Data.Status.Should().Be(ForkchoiceStatus.Success);
                result.Data.PayloadId.Should().Be(null);
                testChain.BlockTree.HeadHash.Should().Be(block.BlockHash);
                testChain.BlockTree.Head!.Number.Should().Be(block.BlockNumber);
                testChain.State.StateRoot.Should().Be(testChain.BlockTree.Head!.StateRoot!);
            }

            async Task CanReorganizeToAnyBlock(MergeTestBlockchain testChain,
                params IReadOnlyList<BlockRequestResult>[] branches)
            {
                foreach (IReadOnlyList<BlockRequestResult>? branch in branches)
                {
                    await CanReorganizeToBlock(branch.Last(), testChain);
                }

                foreach (IReadOnlyList<BlockRequestResult>? branch in branches)
                {
                    foreach (BlockRequestResult block in branch)
                    {
                        await CanReorganizeToBlock(block, testChain);
                    }

                    foreach (BlockRequestResult block in branch.Reverse())
                    {
                        await CanReorganizeToBlock(block, testChain);
                    }
                }
            }

            IReadOnlyList<BlockRequestResult> branch1 =
                await ProduceBranchV1(rpc, chain.BlockTree, 10, CreateParentBlockRequestOnHead(chain.BlockTree), false);
            IReadOnlyList<BlockRequestResult> branch2 =
                await ProduceBranchV1(rpc, chain.BlockTree, 5, branch1[3], false);
            branch2.Last().BlockNumber.Should().Be(1 + 3 + 5);
            IReadOnlyList<BlockRequestResult> branch3 =
                await ProduceBranchV1(rpc, chain.BlockTree, 7, branch1[7], false);
            branch3.Last().BlockNumber.Should().Be(1 + 7 + 7);
            IReadOnlyList<BlockRequestResult> branch4 =
                await ProduceBranchV1(rpc, chain.BlockTree, 3, branch3[4], false);
            branch3.Last().BlockNumber.Should().Be(1 + 7 + 4 + 3);

            await CanReorganizeToAnyBlock(chain, branch1, branch2, branch3, branch4);
        }

        [Test]
        public async Task forkchoiceUpdatedV1_can_prepare_payload_on_any_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);

            async Task CanPrepareBlock(BlockRequestResult block)
            {
                UInt256 timestamp = block.Timestamp;
                Keccak random = Keccak.Zero;
                Address feeRecipient = Address.Zero;
                BlockRequestResult? blockResult = await BuildAndGetPayloadResult(rpc, block.BlockHash, block.ParentHash,
                    block.BlockHash, timestamp, random, feeRecipient);

                blockResult.Should().NotBeNull();
                blockResult!.ParentHash.Should().Be(block.BlockHash);
            }

            async Task CanPrepareOnAnyBlock(params IReadOnlyList<BlockRequestResult>[] branches)
            {
                foreach (IReadOnlyList<BlockRequestResult>? branch in branches)
                {
                    await CanPrepareBlock(branch.Last());
                }

                foreach (IReadOnlyList<BlockRequestResult>? branch in branches)
                {
                    foreach (BlockRequestResult block in branch)
                    {
                        await CanPrepareBlock(block);
                    }

                    foreach (BlockRequestResult block in branch.Reverse())
                    {
                        await CanPrepareBlock(block);
                    }
                }
            }

            IReadOnlyList<BlockRequestResult> branch1 =
                await ProduceBranchV1(rpc, chain.BlockTree, 10, CreateParentBlockRequestOnHead(chain.BlockTree), false);
            IReadOnlyList<BlockRequestResult> branch2 =
                await ProduceBranchV1(rpc, chain.BlockTree, 5, branch1[3], false);
            branch2.Last().BlockNumber.Should().Be(1 + 3 + 5);
            IReadOnlyList<BlockRequestResult> branch3 =
                await ProduceBranchV1(rpc, chain.BlockTree, 7, branch1[7], false);
            branch3.Last().BlockNumber.Should().Be(1 + 7 + 7);
            IReadOnlyList<BlockRequestResult> branch4 =
                await ProduceBranchV1(rpc, chain.BlockTree, 3, branch3[4], false);
            branch3.Last().BlockNumber.Should().Be(1 + 7 + 4 + 3);

            await CanPrepareOnAnyBlock(branch1, branch2, branch3, branch4);
        }

        [Test]
        public async Task executePayloadV1_processes_passed_transactions([Values(false, true)] bool moveHead)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            IReadOnlyList<BlockRequestResult> branch =
                await ProduceBranchV1(rpc, chain.BlockTree, 8, CreateParentBlockRequestOnHead(chain.BlockTree), moveHead);

            foreach (BlockRequestResult block in branch)
            {
                uint count = 10;
                BlockRequestResult executePayloadRequest = CreateBlockRequest(block, TestItem.AddressA);
                PrivateKey from = TestItem.PrivateKeyB;
                Address to = TestItem.AddressD;
                (_, UInt256 toBalanceAfter) = AddTransactions(chain, executePayloadRequest, from, to, count, 1,
                    out BlockHeader? parentHeader);

                executePayloadRequest.GasUsed = GasCostOf.Transaction * count;
                executePayloadRequest.StateRoot =
                    new Keccak("0x3d2e3ced6da0d1e94e65894dc091190480f045647610ef614e1cab4241ca66e0");
                executePayloadRequest.ReceiptsRoot =
                    new Keccak("0xc538d36ed1acf6c28187110a2de3e5df707d6d38982f436eb0db7a623f9dc2cd");
                TryCalculateHash(executePayloadRequest, out Keccak? hash);
                executePayloadRequest.BlockHash = hash;
                ResultWrapper<ExecutePayloadV1Result> result = await rpc.engine_executePayloadV1(executePayloadRequest);
                result.Data.Status.Should().Be(ExecutePayloadStatus.Valid);
                RootCheckVisitor rootCheckVisitor = new();
                chain.StateReader.RunTreeVisitor(rootCheckVisitor, executePayloadRequest.StateRoot);
                rootCheckVisitor.HasRoot.Should().BeTrue();
                // Chain.StateReader.GetBalance(newBlockRequest.StateRoot, from.Address).Should().Be(fromBalanceAfter);
                chain.StateReader.GetBalance(executePayloadRequest.StateRoot, to).Should().Be(toBalanceAfter);
                if (moveHead)
                {
                    ForkchoiceStateV1 forkChoiceUpdatedRequest = new(executePayloadRequest.BlockHash,
                        executePayloadRequest.BlockHash, executePayloadRequest.BlockHash);
                    await rpc.engine_forkchoiceUpdatedV1(forkChoiceUpdatedRequest, null);
                    chain.State.StateRoot.Should().Be(executePayloadRequest.StateRoot);
                    chain.State.StateRoot.Should().NotBe(parentHeader.StateRoot!);
                }
            }
        }

        [Test]
        public async Task executePayloadV1_transactions_produce_receipts()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            IReadOnlyList<BlockRequestResult> branch =
                await ProduceBranchV1(rpc, chain.BlockTree, 1, CreateParentBlockRequestOnHead(chain.BlockTree), false);

            foreach (BlockRequestResult block in branch)
            {
                uint count = 10;
                BlockRequestResult executionPayload = CreateBlockRequest(block, TestItem.AddressA);
                PrivateKey from = TestItem.PrivateKeyB;
                Address to = TestItem.AddressD;
                (_, UInt256 toBalanceAfter) =
                    AddTransactions(chain, executionPayload, from, to, count, 1, out BlockHeader parentHeader);

                executionPayload.GasUsed = GasCostOf.Transaction * count;
                executionPayload.StateRoot =
                    new Keccak("0x3d2e3ced6da0d1e94e65894dc091190480f045647610ef614e1cab4241ca66e0");
                executionPayload.ReceiptsRoot =
                    new Keccak("0xc538d36ed1acf6c28187110a2de3e5df707d6d38982f436eb0db7a623f9dc2cd");
                TryCalculateHash(executionPayload, out Keccak hash);
                executionPayload.BlockHash = hash;
                ResultWrapper<ExecutePayloadV1Result> result = await rpc.engine_executePayloadV1(executionPayload);
                // ToDo we need better way than Task.Delay
                await Task.Delay(10);

                result.Data.Status.Should().Be(ExecutePayloadStatus.Valid);
                RootCheckVisitor rootCheckVisitor = new();
                chain.StateReader.RunTreeVisitor(rootCheckVisitor, executionPayload.StateRoot);
                rootCheckVisitor.HasRoot.Should().BeTrue();
                // ToDo it should be uncommented
                // Chain.StateReader.GetBalance(newBlockRequest.StateRoot, from.Address).Should().Be(fromBalanceAfter);
                chain.StateReader.GetBalance(executionPayload.StateRoot, to).Should().Be(toBalanceAfter);
                Block findBlock = chain.BlockTree.FindBlock(executionPayload.BlockHash, BlockTreeLookupOptions.None)!;
                TxReceipt[]? receipts = chain.ReceiptStorage.Get(findBlock);
                findBlock.Transactions.Select(t => t.Hash).Should().BeEquivalentTo(receipts.Select(r => r.TxHash));
            }
        }

        [Test]
        public async Task getPayloadV1_picks_transactions_from_pool_v1()
        {
            SemaphoreSlim semaphoreSlim = new(0);
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            uint count = 3;
            int value = 10;
            Address recipient = TestItem.AddressF;
            PrivateKey sender = TestItem.PrivateKeyB;
            Transaction[] transactions =
                BuildTransactions(chain, startingHead, sender, recipient, count, value, out _, out _);
            chain.AddTransactions(transactions);
            chain.BlockProducer.BlockProduced += (s, e) =>
            {
                if (e.Block.Transactions.Length == transactions.Length)
                {
                    semaphoreSlim.Release(1);
                }
            };
            string payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes()
                {
                    Timestamp = 100,
                    Random = TestItem.KeccakA,
                    SuggestedFeeRecipient = Address.Zero
                }).Result.Data.PayloadId;
            await semaphoreSlim.WaitAsync(-1);
            await Task.Delay(1000); // ToDo change delay to proper synchronization
            BlockRequestResult getPayloadResult =
                (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;
        

            // ToDo why we need comment here
        //    getPayloadResult.StateRoot.Should().NotBe(chain.BlockTree.Genesis!.StateRoot!);

            Transaction[] transactionsInBlock = getPayloadResult.GetTransactions();
            transactionsInBlock.Should().BeEquivalentTo(transactions,
                o => o.Excluding(t => t.ChainId)
                    .Excluding(t => t.SenderAddress)
                    .Excluding(t => t.Timestamp)
                    .Excluding(t => t.PoolIndex)
                    .Excluding(t => t.GasBottleneck));

            ResultWrapper<ExecutePayloadV1Result> executePayloadResult =
                await rpc.engine_executePayloadV1(getPayloadResult);
            executePayloadResult.Data.Status.Should().Be(ExecutePayloadStatus.Valid);

            UInt256 totalValue = ((int)(count * value)).GWei();
            chain.StateReader.GetBalance(getPayloadResult.StateRoot, recipient).Should().Be(totalValue);
        }

        [Test]
        public async Task getPayloadV1_return_correct_block_values_for_empty_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            Keccak? random = TestItem.KeccakF;
            UInt256 timestamp = chain.BlockTree.Head!.Timestamp + 5;
            Address? suggestedFeeRecipient = TestItem.AddressC;
            PayloadAttributes? payloadAttributes = new PayloadAttributes()
            {
                Random = random, Timestamp = timestamp, SuggestedFeeRecipient = suggestedFeeRecipient
            };
            BlockRequestResult getPayloadResult = await BuildAndGetPayloadResult(chain, rpc, payloadAttributes);
            getPayloadResult.ParentHash.Should().Be(startingHead);


            ResultWrapper<ExecutePayloadV1Result> executePayloadResult =
                await rpc.engine_executePayloadV1(getPayloadResult);
            executePayloadResult.Data.Status.Should().Be(ExecutePayloadStatus.Valid);

            BlockHeader? currentHeader = chain.BlockTree.BestSuggestedHeader!;

            Assert.AreEqual("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347", currentHeader.UnclesHash!.ToString());
            Assert.AreEqual((UInt256)0, currentHeader.Difficulty);
            Assert.AreEqual(0, currentHeader.Nonce);
            Assert.AreEqual(random, currentHeader.MixHash);
        }
        

        private async Task<IReadOnlyList<BlockRequestResult>> ProduceBranchV1(IEngineRpcModule rpc,
            IBlockTree blockTree,
            int count, BlockRequestResult startingParentBlock, bool setHead)
        {
            List<BlockRequestResult> blocks = new();
            BlockRequestResult parentBlock = startingParentBlock;
            for (int i = 0; i < count; i++)
            {
                BlockRequestResult? getPayloadResult = await BuildAndGetPayloadResult(rpc, parentBlock.BlockHash,
                    parentBlock.BlockHash, parentBlock.BlockHash, parentBlock.Timestamp +12,
                    TestItem.KeccakA, Address.Zero);
                ExecutePayloadV1Result executePayloadResponse =
                    (await rpc.engine_executePayloadV1(getPayloadResult)).Data;
                executePayloadResponse.Status.Should().Be(ExecutePayloadStatus.Valid);
                if (setHead)
                {
                    Keccak newHead = getPayloadResult!.BlockHash;
                    ForkchoiceStateV1 forkchoiceStateV1 = new(newHead, newHead, newHead);
                    ResultWrapper<ForkchoiceUpdatedV1Result> setHeadResponse =
                        await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
                    setHeadResponse.Data.Status.Should().Be(ForkchoiceStatus.Success);
                    setHeadResponse.Data.PayloadId.Should().Be(null);
                    blockTree.HeadHash.Should().Be(newHead);
                }

                blocks.Add((getPayloadResult));
                parentBlock = getPayloadResult;
            }

            return blocks;
        }
        
        [Test]
        public async Task blockRequestResult_set_and_get_transactions_roundtrip()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            Keccak startingHead = chain.BlockTree.HeadHash;
            uint count = 3;
            int value = 10;
            Address recipient = TestItem.AddressD;
            PrivateKey sender = TestItem.PrivateKeyB;
            
            Transaction[] txsSource = BuildTransactions(chain, startingHead, sender, recipient, count, value, out _, out _);
            
            BlockRequestResult blockRequestResult = new();
            blockRequestResult.SetTransactions(txsSource);

            Transaction[] txsReceived = blockRequestResult.GetTransactions();

            txsReceived.Should().BeEquivalentTo(txsSource, options => options
                .Excluding(t => t.ChainId)
                .Excluding(t => t.SenderAddress)
                .Excluding(t => t.Timestamp)
            );
        }

        private async Task<BlockRequestResult> SendNewBlockV1(IEngineRpcModule rpc, MergeTestBlockchain chain)
        {
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressD);
            ResultWrapper<ExecutePayloadV1Result> executePayloadResult =
                await rpc.engine_executePayloadV1(blockRequestResult);
            executePayloadResult.Data.Status.Should().Be(ExecutePayloadStatus.Valid);
            return blockRequestResult;
        }

        private async Task<BlockRequestResult> BuildAndGetPayloadResult(
            IEngineRpcModule rpc, Keccak headBlockHash, Keccak finalizedBlockHash, Keccak safeBlockHash,
            UInt256 timestamp, Keccak random, Address feeRecipient, int delay = 0)
        {
            ForkchoiceStateV1 forkchoiceState = new(headBlockHash, finalizedBlockHash, safeBlockHash);
            PayloadAttributes payloadAttributes =
                new() {Timestamp = timestamp, Random = random, SuggestedFeeRecipient = feeRecipient};
            string payloadId = rpc.engine_forkchoiceUpdatedV1(forkchoiceState, payloadAttributes).Result.Data.PayloadId;
            Thread.Sleep(delay);
            ResultWrapper<BlockRequestResult?> getPayloadResult =
                await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));
            return getPayloadResult.Data!;
        }
        
        private async Task<BlockRequestResult> BuildAndGetPayloadResult(MergeTestBlockchain chain,
            IEngineRpcModule rpc, PayloadAttributes payloadAttributes)
        {
            Keccak startingHead = chain.BlockTree.HeadHash;
            Keccak parentHead = chain.BlockTree.Head!.ParentHash!;

            return await BuildAndGetPayloadResult(rpc, startingHead, parentHead, startingHead,
                payloadAttributes.Timestamp, payloadAttributes.Random!, payloadAttributes.SuggestedFeeRecipient);
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

        private void AssertExecutionStatusChangedV1(IEngineRpcModule rpc, Keccak headBlockHash,
            Keccak finalizedBlockHash,
            Keccak confirmedBlockHash)
        {
            ExecutionStatusResult? result = rpc.engine_executionStatus().Data;
            Assert.AreEqual(headBlockHash, result.HeadBlockHash);
            Assert.AreEqual(finalizedBlockHash, result.FinalizedBlockHash);
            Assert.AreEqual(confirmedBlockHash, result.SafeBlockHash);
        }

        private void AssertExecutionStatusNotChangedV1(IEngineRpcModule rpc, Keccak headBlockHash,
            Keccak finalizedBlockHash, Keccak confirmedBlockHash)
        {
            ExecutionStatusResult? result = rpc.engine_executionStatus().Data;
            Assert.AreNotEqual(headBlockHash, result.HeadBlockHash);
            Assert.AreNotEqual(finalizedBlockHash, result.FinalizedBlockHash);
            Assert.AreNotEqual(confirmedBlockHash, result.SafeBlockHash);
        }
    }
}
