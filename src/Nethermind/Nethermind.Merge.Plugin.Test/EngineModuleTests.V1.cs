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
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Test;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.State;
using Nethermind.Trie;
using Newtonsoft.Json;
using NLog;
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
            IPayloadPreparationService payloadPreparationService = Substitute.For<IPayloadPreparationService>();
            Block block = Build.A.Block.WithTransactions(
                new[]
                {
                    Build.A.Transaction.WithTo(TestItem.AddressD)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                    Build.A.Transaction.WithTo(TestItem.AddressD).WithType(TxType.EIP1559).WithMaxFeePerGas(20)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject
                }).TestObject;
            payloadPreparationService.GetPayload(Arg.Any<byte[]>()).Returns(block);
            using MergeTestBlockchain chain = await CreateBlockChain(null, payloadPreparationService);

            IEngineRpcModule rpc = CreateEngineModule(chain);

            string result = RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV1", payload.ToHexString(true));
            Assert.AreEqual(result,
                "{\"jsonrpc\":\"2.0\",\"result\":{\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"feeRecipient\":\"0x0000000000000000000000000000000000000000\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"prevRandao\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"blockNumber\":\"0x0\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"timestamp\":\"0xf4240\",\"extraData\":\"0x010203\",\"baseFeePerGas\":\"0x0\",\"blockHash\":\"0x5fd61518405272d77fd6cdc8a824a109d75343e32024ee4f6769408454b1823d\",\"transactions\":[\"0xf85f800182520894475674cb523a0a2736b7f7534390288fce16982c018025a0634db2f18f24d740be29e03dd217eea5757ed7422680429bdd458c582721b6c2a02f0fa83931c9a99d3448a46b922261447d6a41d8a58992b5596089d15d521102\",\"0x02f8620180011482520894475674cb523a0a2736b7f7534390288fce16982c0180c001a0033e85439a128c42f2ba47ca278f1375ef211e61750018ff21bcd9750d1893f2a04ee981fe5261f8853f95c865232ffdab009abcc7858ca051fb624c49744bf18d\"]},\"id\":67}");
        }

        [Test]
        public async Task processing_block_should_serialize_valid_responses()
        {
            using MergeTestBlockchain chain = await CreateBlockChain(new MergeConfig()
            {
                Enabled = true, FeeRecipient = Address.Zero.ToString(), TerminalTotalDifficulty = "0"
            });
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            Keccak prevRandao = Keccak.Zero;
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
                prevRandao = prevRandao.ToString(),
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
                    $"{{\"jsonrpc\":\"2.0\",\"result\":{{\"payloadStatus\":{{\"status\":\"VALID\",\"latestValidHash\":\"0x1c53bdbf457025f80c6971a9cf50986974eed02f0a9acaeeb49cafef10efd133\",\"validationError\":null}},\"payloadId\":\"{expectedPayloadId.ToHexString(true)}\"}},\"id\":67}}");

            Keccak blockHash = new("0x2de2042d5ab1cf7c89d97f93b1572ddac3c6f77d84b6d44d1d9cec42f76505a7");
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
            result = RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV1", expectedPayloadString);
            result.Should()
                .Be(
                    $"{{\"jsonrpc\":\"2.0\",\"result\":{{\"status\":\"VALID\",\"latestValidHash\":\"{blockHash}\",\"validationError\":null}},\"id\":67}}");

            forkChoiceUpdatedParams = new
            {
                headBlockHash = blockHash.ToString(true),
                safeBlockHash = blockHash.ToString(true),
                finalizedBlockHash = startingHead.ToString(true),
            };
            parameters = new[] { JsonConvert.SerializeObject(forkChoiceUpdatedParams), null };
            // update the fork choice
            result = RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV1", parameters);
            result.Should()
                .Be(
                    "{\"jsonrpc\":\"2.0\",\"result\":{\"payloadStatus\":{\"status\":\"VALID\",\"latestValidHash\":\"0x2de2042d5ab1cf7c89d97f93b1572ddac3c6f77d84b6d44d1d9cec42f76505a7\",\"validationError\":null},\"payloadId\":null},\"id\":67}");
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
            string[] parameters = new[] { JsonConvert.SerializeObject(forkChoiceUpdatedParams) };
            string? result = RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV1", parameters);
            result.Should()
                .Be(
                    "{\"jsonrpc\":\"2.0\",\"result\":{\"payloadStatus\":{\"status\":\"SYNCING\",\"latestValidHash\":null,\"validationError\":null},\"payloadId\":null},\"id\":67}");
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

        [Test]
        public async Task
            engine_forkchoiceUpdatedV1_with_payload_attributes_should_create_block_on_top_of_genesis_and_not_change_head()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            ulong timestamp = 30;
            Keccak random = Keccak.Zero;
            Address feeRecipient = TestItem.AddressD;

            BlockRequestResult? blockRequestResult = await BuildAndGetPayloadResult(rpc, chain, startingHead,
                Keccak.Zero, startingHead, timestamp, random, feeRecipient);

            BlockRequestResult expected = CreateParentBlockRequestOnHead(chain.BlockTree);
            expected.GasLimit = 4000000L;
            expected.BlockHash = new Keccak("0x3ee80ba456bac700bfaf5b2827270406134e2392eb03ec50f6c23de28dd08811");
            expected.LogsBloom = Bloom.Empty;
            expected.FeeRecipient = feeRecipient;
            expected.BlockNumber = 1;
            expected.PrevRandao = random;
            expected.ParentHash = startingHead;
            expected.SetTransactions(Array.Empty<Transaction>());
            expected.Timestamp = timestamp;
            expected.PrevRandao = random;
            expected.ExtraData = Array.Empty<byte>();

            blockRequestResult.Should().BeEquivalentTo(expected);
            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(expected.BlockHash);
            actualHead.Should().Be(startingHead);
        }

        [Test]
        public async Task getPayloadV1_should_return_error_if_there_was_no_corresponding_prepare_call()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;
            string _ = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                    new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data
                .PayloadId!;

            byte[] requestedPayloadId = Bytes.FromHexString("0x45bd36a8143d860d");
            ResultWrapper<BlockRequestResult?> response = await rpc.engine_getPayloadV1(requestedPayloadId);

            response.ErrorCode.Should().Be(MergeErrorCodes.UnavailablePayloadV1);
        }

        [Test]
        public async Task getPayloadV1_should_return_error_if_called_after_timeout()
        {
            const int timeout = 2000;

            MergeConfig mergeConfig = new() { Enabled = true, SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
            using MergeTestBlockchain chain = await CreateBlockChain( mergeConfig);
            chain.PayloadPreparationService = new PayloadPreparationService(chain.PostMergeBlockProducer!, chain.BlockProductionTrigger,
                chain.SealEngine,
                mergeConfig, TimerFactory.Default, chain.LogManager, 1);
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;

            string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                    new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data
                .PayloadId!;

            Thread.Sleep(timeout);

            ResultWrapper<BlockRequestResult?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

            response.ErrorCode.Should().Be(MergeErrorCodes.UnavailablePayloadV1);
        }

        [Test]
        public async Task
            getPayloadBodiesV1_should_return_payload_bodies_in_order_of_request_block_hashes_and_skip_unknown_hashes()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);

            BlockRequestResult blockRequestResult1 = await SendNewBlockV1(rpc, chain);

            PrivateKey from = TestItem.PrivateKeyA;
            Address to = TestItem.AddressB;
            Transaction[] txs = BuildTransactions(chain, blockRequestResult1.BlockHash, from, to, 3, 0, out _, out _);
            chain.AddTransactions(txs);
            BlockRequestResult? blockRequestResult2 = await BuildAndSendNewBlockV1(rpc, chain, true);
            Keccak[] blockHashes =
                new[] { blockRequestResult1.BlockHash, TestItem.KeccakA, blockRequestResult2.BlockHash };
            ExecutionPayloadBodyV1Result[] payloadBodies = rpc.engine_getPayloadBodiesV1(blockHashes).Result.Data;
            ExecutionPayloadBodyV1Result[] expected = new[]
            {
                new ExecutionPayloadBodyV1Result(Array.Empty<Transaction>()), new ExecutionPayloadBodyV1Result(txs)
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
                new ForkchoiceStateV1(notExistingHash, Keccak.Zero, notExistingHash),
                new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random });

            forkchoiceUpdatedV1Response.Data.PayloadStatus.Status.Should()
                .Be(PayloadStatus.Syncing); // ToDo wait for final PostMerge sync
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
                ResultWrapper<PayloadStatusV1> executePayloadResult =
                    await rpc.engine_newPayloadV1(getPayloadResult);
                executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);
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
                ResultWrapper<PayloadStatusV1>? executePayloadResult =
                    await rpc.engine_newPayloadV1(getPayloadResult);
                executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);
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
            PayloadAttributes? payloadAttributes = new()
            {
                PrevRandao = random, SuggestedFeeRecipient = feeRecipient, Timestamp = timestamp
            };
            ForkchoiceStateV1? forkchoiceStateV1 = new(currentHead, currentHead, currentHead);
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
                yield return GetNewBlockRequestBadDataTestCase(r => r.ReceiptsRoot, TestItem.KeccakD);
                yield return GetNewBlockRequestBadDataTestCase(r => r.StateRoot, TestItem.KeccakD);

                Bloom bloom = new();
                bloom.Add(new[]
                {
                    Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakG).TestObject
                });
                yield return GetNewBlockRequestBadDataTestCase(r => r.LogsBloom, bloom);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Transactions, new[] { new byte[] { 1 } });
                yield return GetNewBlockRequestBadDataTestCase(r => r.GasUsed, 1);
            }
        }
        
        [Test]
        public async Task executePayloadV1_unknown_parentHash_return_accepted()
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

            ResultWrapper<PayloadStatusV1>
                executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
            executePayloadResult.Data.Status.Should().Be(PayloadStatus.Accepted);
        }

        [TestCaseSource(nameof(WrongInputTestsV1))]
        public async Task executePayloadV1_rejects_incorrect_input(Action<BlockRequestResult> breakerAction)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
            breakerAction(getPayloadResult);
            if (TryCalculateHash(getPayloadResult, out Keccak? hash))
            {
                getPayloadResult.BlockHash = hash;
            }

            ResultWrapper<PayloadStatusV1>
                executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
            executePayloadResult.Data.Status.Should().Be(PayloadStatus.Invalid);
        }

        [Test]
        public async Task executePayloadV1_rejects_invalid_blockHash()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
            getPayloadResult.BlockHash = TestItem.KeccakC;

            ResultWrapper<PayloadStatusV1>
                executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
            executePayloadResult.Data.Status.Should().Be(PayloadStatus.InvalidBlockHash);
        }


        [Test]
        public async Task executePayloadV1_accepts_already_known_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Block block = Build.A.Block.WithNumber(1).WithParent(chain.BlockTree.Head!).WithDifficulty(0).WithNonce(0)
                .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
                .TestObject;
            block.Header.IsPostMerge = true;
            block.Header.Hash = block.CalculateHash();
            await chain.BlockTree.SuggestBlockAsync(block!);
            BlockRequestResult blockRequest = new(block);
            ResultWrapper<PayloadStatusV1> executePayloadResult =
                await rpc.engine_newPayloadV1(blockRequest);
            executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);
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
            forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
            forkchoiceUpdatedResult.Data.PayloadId.Should().Be(null);

            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(startingHead);
            actualHead.Should().Be(newHeadHash);
            AssertExecutionStatusChanged(rpc, newHeadHash!, Keccak.Zero, startingHead);
        }
        
        [Test]
        public async Task forkchoiceUpdatedV1_should_update_finalized_block_hash()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            TestRpcBlockchain testRpc = await CreateTestRpc(chain);
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockRequestResult blockRequestResult = await SendNewBlockV1(rpc, chain);

            Keccak newHeadHash = blockRequestResult.BlockHash;
            ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash!, startingHead, startingHead!);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
            forkchoiceUpdatedResult.Data.PayloadId.Should().Be(null);
            
            Keccak? actualFinalizedHash = chain.BlockTree.FinalizedHash;
            actualFinalizedHash.Should().NotBeNull();
            actualFinalizedHash.Should().Be(startingHead);
            
            BlockForRpc blockForRpc = testRpc.EthRpcModule.eth_getBlockByNumber(BlockParameter.Finalized).Data;
            blockForRpc.Should().NotBeNull();
            actualFinalizedHash = blockForRpc.Hash;
            actualFinalizedHash.Should().NotBeNull();
            actualFinalizedHash.Should().Be(startingHead);
            
            AssertExecutionStatusChanged(rpc, newHeadHash!, startingHead, startingHead);
        }
        
        [Test]
        public async Task forkchoiceUpdatedV1_should_update_safe_block_hash()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            TestRpcBlockchain testRpc = await CreateTestRpc(chain);
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockRequestResult blockRequestResult = await SendNewBlockV1(rpc, chain);

            Keccak newHeadHash = blockRequestResult.BlockHash;
            ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash!, startingHead, startingHead!);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
            forkchoiceUpdatedResult.Data.PayloadId.Should().Be(null);
            
            Keccak? actualSafeHash = chain.BlockTree.SafeHash;
            actualSafeHash.Should().NotBeNull();
            actualSafeHash.Should().Be(startingHead);
            
            BlockForRpc blockForRpc = testRpc.EthRpcModule.eth_getBlockByNumber(BlockParameter.Safe).Data;
            blockForRpc.Should().NotBeNull();
            actualSafeHash = blockForRpc.Hash;
            actualSafeHash.Should().NotBeNull();
            actualSafeHash.Should().Be(startingHead);
            
            AssertExecutionStatusChanged(rpc, newHeadHash!, startingHead, startingHead);
        }
        
        
        [Test]
        public async Task forkchoiceUpdatedV1_should_work_with_zero_keccak_as_safe_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockRequestResult blockRequestResult = await SendNewBlockV1(rpc, chain);

            Keccak newHeadHash = blockRequestResult.BlockHash;
            ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash!, newHeadHash!, Keccak.Zero);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
            forkchoiceUpdatedResult.Data.PayloadId.Should().Be(null);

            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(startingHead);
            actualHead.Should().Be(newHeadHash);
            AssertExecutionStatusChanged(rpc, newHeadHash!, newHeadHash!, Keccak.Zero);
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
            forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
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
            forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
                .Be(nameof(PayloadStatus.Syncing).ToUpper()); // ToDo wait for final PostMerge sync
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
            forkchoiceUpdatedResult.ErrorCode.Should()
                .Be(MergeErrorCodes.InvalidForkchoiceState);

            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(newHeadHash);
        }

        [Test]
        public async Task forkChoiceUpdatedV1_no_common_branch_failsk()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak? startingHead = chain.BlockTree.HeadHash;
            Block parent = Build.A.Block.WithNumber(2).WithParentHash(TestItem.KeccakA).WithNonce(0).WithDifficulty(0).TestObject;
            Block block = Build.A.Block.WithNumber(3).WithParent(parent).WithNonce(0).WithDifficulty(0).TestObject;
            
            await rpc.engine_newPayloadV1(new BlockRequestResult(parent));
            
            ForkchoiceStateV1 forkchoiceStateV1 = new(parent.Hash!, startingHead, startingHead);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
                .Be("SYNCING");
            
            await rpc.engine_newPayloadV1(new BlockRequestResult(block));
            
            ForkchoiceStateV1 forkchoiceStateV1_1 = new(parent.Hash!, startingHead, startingHead);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult_1 =
                await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1_1, null);
            forkchoiceUpdatedResult_1.Data.PayloadStatus.Status.Should()
                .Be("SYNCING");
            
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
            forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
            forkchoiceUpdatedResult.Data.PayloadId.Should().Be(null);
            AssertExecutionStatusChangedV1(rpc, newHeadHash, newHeadHash, newHeadHash);
        }

        [Test]
        public async Task Can_transition_from_PoW_chain()
        {
            using MergeTestBlockchain chain =
                await CreateBlockChain(new MergeConfig() { Enabled = true, TerminalTotalDifficulty = "1000001" });
            IEngineRpcModule rpc = CreateEngineModule(chain);

            // adding PoW block
            await chain.AddBlock();

            // creating PoS block
            Block? head = chain.BlockTree.Head;
            BlockRequestResult blockRequestResult = await SendNewBlockV1(rpc, chain);
            await rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(blockRequestResult.BlockHash, blockRequestResult.BlockHash,
                    blockRequestResult.BlockHash), null);
            Assert.AreEqual(2, chain.BlockTree.Head!.Number);
        }

        [TestCase(null)]
        [TestCase(1000000000)]
        [TestCase(1000001)]
        public async Task executePayloadV1_should_not_accept_blocks_with_incorrect_ttd(long? terminalTotalDifficulty)
        {
            using MergeTestBlockchain chain = await CreateBlockChain(new MergeConfig()
            {
                Enabled = true, TerminalTotalDifficulty = $"{terminalTotalDifficulty}"
            });
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressD);
            ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(blockRequestResult);
            resultWrapper.Data.Status.Should().Be(PayloadStatus.Invalid);
            resultWrapper.Data.LatestValidHash.Should().Be(Keccak.Zero);
        }

        [TestCase(null)]
        [TestCase(1000000000)]
        [TestCase(1000001)]
        public async Task forkchoiceUpdatedV1_should_not_accept_blocks_with_incorrect_ttd(long? terminalTotalDifficulty)
        {
            using MergeTestBlockchain chain = await CreateBlockChain(new MergeConfig()
            {
                Enabled = true, TerminalTotalDifficulty = $"{terminalTotalDifficulty}"
            });
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak blockHash = chain.BlockTree.HeadHash;
            ResultWrapper<ForkchoiceUpdatedV1Result> resultWrapper =
                await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(blockHash, blockHash, blockHash), null);
            resultWrapper.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Invalid);
            resultWrapper.Data.PayloadStatus.LatestValidHash.Should().Be(Keccak.Zero);
        }

        [Test]
        public async Task executePayloadV1_accepts_first_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressD);
            ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(blockRequestResult);
            resultWrapper.Data.Status.Should().Be(PayloadStatus.Valid);
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
            ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(blockRequestResult);
            resultWrapper.Data.Status.Should().Be(PayloadStatus.Valid);
            ResultWrapper<PayloadStatusV1>
                resultWrapper2 = await rpc.engine_newPayloadV1(blockRequestResult);
            resultWrapper2.Data.Status.Should().Be(PayloadStatus.Valid);
            blockRequestResult.ParentHash = blockRequestResult.BlockHash;
            ResultWrapper<PayloadStatusV1> invalidBlockRequest =
                await rpc.engine_newPayloadV1(blockRequestResult);
            invalidBlockRequest.Data.Status.Should().Be(PayloadStatus.InvalidBlockHash);
        }

        [TestCase(30)]
        public async Task can_progress_chain_one_by_one_v1(int count)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak lastHash = (await ProduceBranchV1(rpc, chain, count,
                    CreateParentBlockRequestOnHead(chain.BlockTree), true))
                .Last()
                .BlockHash;
            chain.BlockTree.HeadHash.Should().Be(lastHash);
            Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, b => b.IsGenesis, true);
            last.Should().NotBeNull();
            last!.IsGenesis.Should().BeTrue();
        }

        [Test]
        public async Task forkchoiceUpdatedV1_can_reorganize_to_last_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);

            async Task CanReorganizeToBlock(BlockRequestResult block, MergeTestBlockchain testChain)
            {
                ForkchoiceStateV1 forkchoiceStateV1 =
                    new(block.BlockHash, block.BlockHash, block.BlockHash);
                ResultWrapper<ForkchoiceUpdatedV1Result> result =
                    await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
                result.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
                result.Data.PayloadId.Should().Be(null);
                testChain.BlockTree.HeadHash.Should().Be(block.BlockHash);
                testChain.BlockTree.Head!.Number.Should().Be(block.BlockNumber);
                testChain.State.StateRoot.Should().Be(testChain.BlockTree.Head!.StateRoot!);
            }

            async Task CanReorganizeToLastBlock(MergeTestBlockchain testChain,
                params IReadOnlyList<BlockRequestResult>[] branches)
            {
                foreach (IReadOnlyList<BlockRequestResult>? branch in branches)
                {
                    await CanReorganizeToBlock(branch.Last(), testChain);
                }
            }

            IReadOnlyList<BlockRequestResult> branch1 =
                await ProduceBranchV1(rpc, chain, 10, CreateParentBlockRequestOnHead(chain.BlockTree), true);
            IReadOnlyList<BlockRequestResult> branch2 =
                await ProduceBranchV1(rpc, chain, 6, branch1[3], true, TestItem.KeccakC);

            await CanReorganizeToLastBlock(chain, branch1, branch2);
        }
        
        [Test]
        public async Task forkchoiceUpdatedV1_head_block_after_reorg()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);

            async Task CanReorganizeToBlock(BlockRequestResult block, MergeTestBlockchain testChain)
            {
                ForkchoiceStateV1 forkchoiceStateV1 =
                    new(block.BlockHash, block.BlockHash, block.BlockHash);
                ResultWrapper<ForkchoiceUpdatedV1Result> result =
                    await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
                result.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
                result.Data.PayloadId.Should().Be(null);
                testChain.BlockTree.HeadHash.Should().Be(block.BlockHash);
                testChain.BlockTree.Head!.Number.Should().Be(block.BlockNumber);
                testChain.State.StateRoot.Should().Be(testChain.BlockTree.Head!.StateRoot!);
            }

            IReadOnlyList<BlockRequestResult> branch1 =
                await ProduceBranchV1(rpc, chain, 10, CreateParentBlockRequestOnHead(chain.BlockTree), true);
            IReadOnlyList<BlockRequestResult> branch2 =
                await ProduceBranchV1(rpc, chain, 6, branch1[3], true, TestItem.KeccakC);

            await CanReorganizeToBlock(branch2.Last(), chain);
        }

        [Test]
        public async Task newPayloadV1_should_return_accepted_for_side_branch()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressD);
            ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(blockRequestResult);
            resultWrapper.Data.Status.Should().Be(PayloadStatus.Valid);
            ForkchoiceStateV1 forkChoiceUpdatedRequest = new(blockRequestResult.BlockHash,
                blockRequestResult.BlockHash, blockRequestResult.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> fcu1 = (await rpc.engine_forkchoiceUpdatedV1(forkChoiceUpdatedRequest,
                new PayloadAttributes()
                {
                    PrevRandao = TestItem.KeccakA,
                    SuggestedFeeRecipient = Address.Zero,
                    Timestamp = blockRequestResult.Timestamp + 1
                }));
            await rpc.engine_getPayloadV1(Bytes.FromHexString(fcu1.Data.PayloadId!));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task executePayloadV1_processes_passed_transactions(bool moveHead)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            IReadOnlyList<BlockRequestResult> branch =
                await ProduceBranchV1(rpc, chain, 8, CreateParentBlockRequestOnHead(chain.BlockTree),
                    moveHead);

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
                ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV1(executePayloadRequest);
                result.Data.Status.Should().Be(PayloadStatus.Valid);
                RootCheckVisitor rootCheckVisitor = new();
                chain.StateReader.RunTreeVisitor(rootCheckVisitor, executePayloadRequest.StateRoot);
                rootCheckVisitor.HasRoot.Should().BeTrue();
                
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
                await ProduceBranchV1(rpc, chain, 1, CreateParentBlockRequestOnHead(chain.BlockTree), false);

            foreach (BlockRequestResult block in branch)
            {
                uint count = 10;
                BlockRequestResult executionPayload = CreateBlockRequest(block, TestItem.AddressA);
                PrivateKey from = TestItem.PrivateKeyB;
                Address to = TestItem.AddressD;
                (_, UInt256 toBalanceAfter) =
                    AddTransactions(chain, executionPayload, from, to, count, 1, out BlockHeader parentHeader);

                UInt256 fromBalance = chain.StateReader.GetBalance(parentHeader.StateRoot!, from.Address);
                executionPayload.GasUsed = GasCostOf.Transaction * count;
                executionPayload.StateRoot =
                    new Keccak("0x3d2e3ced6da0d1e94e65894dc091190480f045647610ef614e1cab4241ca66e0");
                executionPayload.ReceiptsRoot =
                    new Keccak("0xc538d36ed1acf6c28187110a2de3e5df707d6d38982f436eb0db7a623f9dc2cd");
                TryCalculateHash(executionPayload, out Keccak hash);
                executionPayload.BlockHash = hash;
                ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV1(executionPayload);

                result.Data.Status.Should().Be(PayloadStatus.Valid);
                RootCheckVisitor rootCheckVisitor = new();
                chain.StateReader.RunTreeVisitor(rootCheckVisitor, executionPayload.StateRoot);
                rootCheckVisitor.HasRoot.Should().BeTrue();

                UInt256 fromBalanceAfter = chain.StateReader.GetBalance(executionPayload.StateRoot, from.Address); 
                Assert.True(fromBalanceAfter < fromBalance - toBalanceAfter);
                chain.StateReader.GetBalance(executionPayload.StateRoot, to).Should().Be(toBalanceAfter);
                Block findBlock = chain.BlockTree.FindBlock(executionPayload.BlockHash, BlockTreeLookupOptions.None)!;
                TxReceipt[]? receipts = chain.ReceiptStorage.Get(findBlock);
                findBlock.Transactions.Select(t => t.Hash).Should().BeEquivalentTo(receipts.Select(r => r.TxHash));
            }
        }

        [Test]
        public async Task getPayloadV1_picks_transactions_from_pool_v1()
        {
            SemaphoreSlim blockImprovementLock = new(0);
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
            chain.PayloadPreparationService!.BlockImproved += (s, e) =>
            {
                blockImprovementLock.Release(1);
            };
            string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes()
                {
                    Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero
                }).Result.Data.PayloadId!;
            await blockImprovementLock.WaitAsync(10000);
            BlockRequestResult getPayloadResult =
                (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId!))).Data!;

            getPayloadResult.StateRoot.Should().NotBe(chain.BlockTree.Genesis!.StateRoot!);

            Transaction[] transactionsInBlock = getPayloadResult.GetTransactions();
            transactionsInBlock.Should().BeEquivalentTo(transactions,
                o => o.Excluding(t => t.ChainId)
                    .Excluding(t => t.SenderAddress)
                    .Excluding(t => t.Timestamp)
                    .Excluding(t => t.PoolIndex)
                    .Excluding(t => t.GasBottleneck));

            ResultWrapper<PayloadStatusV1> executePayloadResult =
                await rpc.engine_newPayloadV1(getPayloadResult);
            executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

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
            PayloadAttributes? payloadAttributes = new()
            {
                PrevRandao = random, Timestamp = timestamp, SuggestedFeeRecipient = suggestedFeeRecipient
            };
            BlockRequestResult getPayloadResult = await BuildAndGetPayloadResult(chain, rpc, payloadAttributes);
            getPayloadResult.ParentHash.Should().Be(startingHead);


            ResultWrapper<PayloadStatusV1> executePayloadResult =
                await rpc.engine_newPayloadV1(getPayloadResult);
            executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

            BlockHeader? currentHeader = chain.BlockTree.BestSuggestedHeader!;

            Assert.AreEqual("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347",
                currentHeader.UnclesHash!.ToString());
            Assert.AreEqual((UInt256)0, currentHeader.Difficulty);
            Assert.AreEqual(0, currentHeader.Nonce);
            Assert.AreEqual(random, currentHeader.MixHash);
        }


        private async Task<IReadOnlyList<BlockRequestResult>> ProduceBranchV1(IEngineRpcModule rpc,
            MergeTestBlockchain chain,
            int count, BlockRequestResult startingParentBlock, bool setHead, Keccak? random = null)
        {
            List<BlockRequestResult> blocks = new();
            BlockRequestResult parentBlock = startingParentBlock;
            parentBlock.TryGetBlock(out Block? block);
            UInt256? startingTotalDifficulty = block!.IsGenesis
                ? block.Difficulty : chain.BlockFinder.FindHeader(block!.Header!.ParentHash!)!.TotalDifficulty;
            BlockHeader parentHeader = block!.Header;
            parentHeader.TotalDifficulty = startingTotalDifficulty +
                                           parentHeader.Difficulty;
            for (int i = 0; i < count; i++)
            {
                BlockRequestResult? getPayloadResult = await BuildAndGetPayloadOnBranch(rpc, chain, parentHeader,
                    parentBlock.Timestamp + 12,
                    random ?? TestItem.KeccakA, Address.Zero);
                PayloadStatusV1 payloadStatusResponse =
                    (await rpc.engine_newPayloadV1(getPayloadResult)).Data;
                payloadStatusResponse.Status.Should().Be(PayloadStatus.Valid);
                if (setHead)
                {
                    Keccak newHead = getPayloadResult!.BlockHash;
                    ForkchoiceStateV1 forkchoiceStateV1 = new(newHead, newHead, newHead);
                    ResultWrapper<ForkchoiceUpdatedV1Result> setHeadResponse =
                        await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
                    setHeadResponse.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
                    setHeadResponse.Data.PayloadId.Should().Be(null);
                }

                blocks.Add((getPayloadResult));
                parentBlock = getPayloadResult;
                parentBlock.TryGetBlock(out block!);
                block.Header.TotalDifficulty = parentHeader.TotalDifficulty + block.Header.Difficulty;
                parentHeader = block.Header;
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

            Transaction[] txsSource =
                BuildTransactions(chain, startingHead, sender, recipient, count, value, out _, out _);

            BlockRequestResult blockRequestResult = new();
            blockRequestResult.SetTransactions(txsSource);

            Transaction[] txsReceived = blockRequestResult.GetTransactions();

            txsReceived.Should().BeEquivalentTo(txsSource, options => options
                .Excluding(t => t.ChainId)
                .Excluding(t => t.SenderAddress)
                .Excluding(t => t.Timestamp)
            );
        }

        [Test]
        public async Task payloadV1_suggestedFeeRecipient_in_config()
        {
            using MergeTestBlockchain chain =
                await CreateBlockChain(new MergeConfig()
                {
                    Enabled = true, FeeRecipient = TestItem.AddressB.ToString(), TerminalTotalDifficulty = "0"
                });
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = TestItem.AddressC;
            string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                    new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data
                .PayloadId!;
            (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!.FeeRecipient.Should()
                .Be(TestItem.AddressB);
        }

        [Test]
        public async Task payloadV1_no_suggestedFeeRecipient_in_config()
        {
            using MergeTestBlockchain chain =
                await CreateBlockChain(new MergeConfig() { Enabled = true, TerminalTotalDifficulty = "0" });
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = TestItem.AddressC;
            string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                    new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data
                .PayloadId!;
            (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!.FeeRecipient.Should()
                .Be(TestItem.AddressC);
        }
        
        [TestCase(0, "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(1000001, "0x191dc9697d77129ee5b6f6d57074d2c854a38129913e3fdd3d9f0ebc930503a6")]
        public async Task exchangeTransitionConfiguration_return_expected_results(long clTtd, string terminalBlockHash)
        {
            using MergeTestBlockchain chain =
                await CreateBlockChain(new MergeConfig() { Enabled = true, TerminalTotalDifficulty = "1000001", TerminalBlockHash = new Keccak("0x191dc9697d77129ee5b6f6d57074d2c854a38129913e3fdd3d9f0ebc930503a6").ToString(true), TerminalBlockNumber = 1 });
            IEngineRpcModule rpc = CreateEngineModule(chain);
            
            TransitionConfigurationV1 result = rpc.engine_exchangeTransitionConfigurationV1(new TransitionConfigurationV1()
            {
                TerminalBlockNumber = 0,
                TerminalBlockHash = new Keccak(terminalBlockHash),
                TerminalTotalDifficulty = (UInt256)clTtd
            }).Data;
            
            Assert.AreEqual((UInt256)1000001, result.TerminalTotalDifficulty);
            Assert.AreEqual(1, result.TerminalBlockNumber);
            Assert.AreEqual("0x191dc9697d77129ee5b6f6d57074d2c854a38129913e3fdd3d9f0ebc930503a6", result.TerminalBlockHash.ToString());
        }
        
        [TestCase(0, "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(1000001, "0x191dc9697d77129ee5b6f6d57074d2c854a38129913e3fdd3d9f0ebc930503a6")]
        public async Task exchangeTransitionConfiguration_return_with_empty_Nethermind_configuration(long clTtd, string terminalBlockHash)
        {
            using MergeTestBlockchain chain =
                await CreateBlockChain(new MergeConfig() { Enabled = true });
            IEngineRpcModule rpc = CreateEngineModule(chain);
            
            TransitionConfigurationV1 result = rpc.engine_exchangeTransitionConfigurationV1(new TransitionConfigurationV1()
            {
                TerminalBlockNumber = 0,
                TerminalBlockHash = new Keccak(terminalBlockHash),
                TerminalTotalDifficulty = (UInt256)clTtd
            }).Data;
            
            Assert.AreEqual((UInt256)0, result.TerminalTotalDifficulty);
            Assert.AreEqual(0, result.TerminalBlockNumber);
            Assert.AreEqual("0x0000000000000000000000000000000000000000000000000000000000000000", result.TerminalBlockHash.ToString());
        }

        private async Task<BlockRequestResult> SendNewBlockV1(IEngineRpcModule rpc, MergeTestBlockchain chain)
        {
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressD);
            ResultWrapper<PayloadStatusV1> executePayloadResult =
                await rpc.engine_newPayloadV1(blockRequestResult);
            executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);
            return blockRequestResult;
        }

        private async Task<BlockRequestResult> BuildAndSendNewBlockV1(IEngineRpcModule rpc, MergeTestBlockchain chain, bool waitForBlockImprovement)
        {
            Keccak head = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;
            BlockRequestResult blockRequestResult = await BuildAndGetPayloadResult(rpc, chain, head,
                Keccak.Zero, head, timestamp, random, feeRecipient, waitForBlockImprovement);
            ResultWrapper<PayloadStatusV1> executePayloadResult =
                await rpc.engine_newPayloadV1(blockRequestResult);
            executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);
            return blockRequestResult;
        }

        private async Task<BlockRequestResult> BuildAndGetPayloadOnBranch(
            IEngineRpcModule rpc, MergeTestBlockchain chain, BlockHeader parentHeader,
            UInt256 timestamp, Keccak random, Address feeRecipient)
        {
            PayloadAttributes payloadAttributes =
                new() { Timestamp = timestamp, PrevRandao = random, SuggestedFeeRecipient = feeRecipient };

            // we're using payloadService directly, because we can't use fcU for branch
            string payloadId = chain.PayloadPreparationService!.StartPreparingPayload(parentHeader, payloadAttributes)!;

            ResultWrapper<BlockRequestResult?> getPayloadResult =
                await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));
            return getPayloadResult.Data!;
        }


        [Test]
        public async Task repeat_the_same_payload_after_fcu_should_return_valid_and_be_ignored()
        {
            using MergeTestBlockchain chain =
                await CreateBlockChain(new MergeConfig() { Enabled = true, TerminalTotalDifficulty = "0" });
            IEngineRpcModule rpc = CreateEngineModule(chain);

            // Correct new payload
            BlockRequestResult blockRequestResult1 = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressA);
            ResultWrapper<PayloadStatusV1> newPayloadResult1 = await rpc.engine_newPayloadV1(blockRequestResult1);
            newPayloadResult1.Data.Status.Should().Be(PayloadStatus.Valid);

            // Fork choice updated with first np hash
            ForkchoiceStateV1 forkChoiceState1 = new ForkchoiceStateV1(blockRequestResult1.BlockHash,
                blockRequestResult1.BlockHash,
                blockRequestResult1.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult1 =
                await rpc.engine_forkchoiceUpdatedV1(forkChoiceState1);
            forkchoiceUpdatedResult1.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
            
            ResultWrapper<PayloadStatusV1> newPayloadResult2 = await rpc.engine_newPayloadV1(blockRequestResult1);
            newPayloadResult2.Data.Status.Should().Be(PayloadStatus.Valid);
            newPayloadResult2.Data.LatestValidHash.Should().Be(blockRequestResult1.BlockHash);
        }

        [Test]
        public async Task payloadV1_invalid_parent_hash()
        {
            using MergeTestBlockchain chain =
                await CreateBlockChain(new MergeConfig() {Enabled = true, TerminalTotalDifficulty = "0"});
            IEngineRpcModule rpc = CreateEngineModule(chain);

            // Correct new payload
            BlockRequestResult blockRequestResult1 = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressA);
            ResultWrapper<PayloadStatusV1> newPayloadResult1 = await rpc.engine_newPayloadV1(blockRequestResult1);
            newPayloadResult1.Data.Status.Should().Be(PayloadStatus.Valid);

            // Fork choice updated with first np hash
            ForkchoiceStateV1 forkChoiceState1 = new ForkchoiceStateV1(blockRequestResult1.BlockHash, blockRequestResult1.BlockHash,
                blockRequestResult1.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult1 = await rpc.engine_forkchoiceUpdatedV1(forkChoiceState1);
            forkchoiceUpdatedResult1.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);

            // New payload unknown parent hash
            BlockRequestResult blockRequestResult2A = CreateBlockRequest(blockRequestResult1, TestItem.AddressA);
            blockRequestResult2A.ParentHash = TestItem.KeccakB;
            TryCalculateHash(blockRequestResult2A, out Keccak? hash);
            blockRequestResult2A.BlockHash = hash;
            ResultWrapper<PayloadStatusV1> newPayloadResult2A = await rpc.engine_newPayloadV1(blockRequestResult2A);
            newPayloadResult2A.Data.Status.Should().Be(PayloadStatus.Accepted);

            // Fork choice updated with unknown parent hash
            ForkchoiceStateV1 forkChoiceState2A = new ForkchoiceStateV1(blockRequestResult2A.BlockHash,
                blockRequestResult2A.BlockHash,
                blockRequestResult2A.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult2A = await rpc.engine_forkchoiceUpdatedV1(forkChoiceState2A);
            forkchoiceUpdatedResult2A.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Syncing);

            // New payload with correct parent hash
            BlockRequestResult blockRequestResult2B = CreateBlockRequest(blockRequestResult1, TestItem.AddressA);
            ResultWrapper<PayloadStatusV1> newPayloadResult2B = await rpc.engine_newPayloadV1(blockRequestResult2B);
            newPayloadResult2B.Data.Status.Should().Be(PayloadStatus.Valid);

            // Fork choice updated with correct parent hash
            ForkchoiceStateV1 forkChoiceState2B = new ForkchoiceStateV1(blockRequestResult2B.BlockHash, blockRequestResult2B.BlockHash,
                blockRequestResult2B.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult2B = await rpc.engine_forkchoiceUpdatedV1(forkChoiceState2B);
            forkchoiceUpdatedResult2B.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
            
            // New payload unknown parent hash
            BlockRequestResult blockRequestResult3A = CreateBlockRequest(blockRequestResult2A, TestItem.AddressA);
            ResultWrapper<PayloadStatusV1> newPayloadResult3A = await rpc.engine_newPayloadV1(blockRequestResult3A);
            newPayloadResult3A.Data.Status.Should().Be(PayloadStatus.Accepted);

            // Fork choice updated with unknown parent hash
            ForkchoiceStateV1 forkChoiceState3A = new ForkchoiceStateV1(blockRequestResult3A.BlockHash,
                blockRequestResult3A.BlockHash,
                blockRequestResult3A.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult3A = await rpc.engine_forkchoiceUpdatedV1(forkChoiceState3A);
            forkchoiceUpdatedResult3A.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Syncing);
            
            BlockRequestResult blockRequestResult3B = CreateBlockRequest(blockRequestResult2B, TestItem.AddressA);
            ResultWrapper<PayloadStatusV1> newPayloadResult3B = await rpc.engine_newPayloadV1(blockRequestResult3B);
            newPayloadResult3B.Data.Status.Should().Be(PayloadStatus.Valid);

            // Fork choice updated with correct parent hash
            ForkchoiceStateV1 forkChoiceState3B = new ForkchoiceStateV1(blockRequestResult3B.BlockHash, blockRequestResult3B.BlockHash,
                blockRequestResult3B.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult3B = await rpc.engine_forkchoiceUpdatedV1(forkChoiceState3B);
            forkchoiceUpdatedResult3B.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
        }

        [Test]
        public async Task payloadV1_latest_block_after_reorg()
        {
            using MergeTestBlockchain chain =
                await CreateBlockChain(new MergeConfig() {Enabled = true, TerminalTotalDifficulty = "0"});
            IEngineRpcModule rpc = CreateEngineModule(chain);

            Keccak prevRandao1 = TestItem.KeccakA;
            Keccak prevRandao2 = TestItem.KeccakB;
            Keccak prevRandao3 = TestItem.KeccakC;

            {
                ForkchoiceStateV1 forkChoiceStateGen = new(chain.BlockTree.Head!.Hash!, chain.BlockTree.Head!.Hash!,
                    chain.BlockTree.Head!.Hash!);
                ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResultGen =
                    await rpc.engine_forkchoiceUpdatedV1(forkChoiceStateGen,
                        new PayloadAttributes()
                        {
                            Timestamp = Timestamper.UnixTime.Seconds,
                            PrevRandao = prevRandao1,
                            SuggestedFeeRecipient = Address.Zero
                        });
                forkchoiceUpdatedResultGen.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
            }

            // Add one block
            BlockRequestResult blockRequestResult1 = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressA);
            blockRequestResult1.PrevRandao = prevRandao1;

            TryCalculateHash(blockRequestResult1, out Keccak? hash1);
            blockRequestResult1.BlockHash = hash1;

            ResultWrapper<PayloadStatusV1> newPayloadResult1 = await rpc.engine_newPayloadV1(blockRequestResult1);
            newPayloadResult1.Data.Status.Should().Be(PayloadStatus.Valid);

            ForkchoiceStateV1 forkChoiceState1 = new(blockRequestResult1.BlockHash,
                blockRequestResult1.BlockHash, blockRequestResult1.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult1 =
                await rpc.engine_forkchoiceUpdatedV1(forkChoiceState1,
                    new PayloadAttributes()
                    {
                        Timestamp = Timestamper.UnixTime.Seconds,
                        PrevRandao = prevRandao2,
                        SuggestedFeeRecipient = Address.Zero
                    });
            forkchoiceUpdatedResult1.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);


            {
                BlockRequestResult blockRequestResult2 = CreateBlockRequest(
                    blockRequestResult1,
                    TestItem.AddressA);

                blockRequestResult2.PrevRandao = prevRandao3;

                TryCalculateHash(blockRequestResult2, out Keccak? hash);
                blockRequestResult2.BlockHash = hash;

                ResultWrapper<PayloadStatusV1> newPayloadResult2 = await rpc.engine_newPayloadV1(blockRequestResult2);
                newPayloadResult2.Data.Status.Should().Be(PayloadStatus.Valid);

                ForkchoiceStateV1 forkChoiceState2 = new(blockRequestResult2.BlockHash,
                    blockRequestResult1.BlockHash, blockRequestResult1.BlockHash);
                ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult2 =
                    await rpc.engine_forkchoiceUpdatedV1(forkChoiceState2);
                forkchoiceUpdatedResult2.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);

                Keccak currentBlockHash = chain.BlockTree.Head!.Hash!;
                Assert.True(currentBlockHash == blockRequestResult2.BlockHash);
            }

            // re-org
            {
                BlockRequestResult blockRequestResult3 = CreateBlockRequest(
                    blockRequestResult1,
                    TestItem.AddressA);

                blockRequestResult3.PrevRandao = prevRandao2;

                TryCalculateHash(blockRequestResult3, out Keccak? hash);
                blockRequestResult3.BlockHash = hash;

                ResultWrapper<PayloadStatusV1> newPayloadResult3 = await rpc.engine_newPayloadV1(blockRequestResult3);
                newPayloadResult3.Data.Status.Should().Be(PayloadStatus.Valid);

                ForkchoiceStateV1 forkChoiceState3 = new (blockRequestResult3.BlockHash,
                    blockRequestResult1.BlockHash, blockRequestResult1.BlockHash);
                ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult3 =
                    await rpc.engine_forkchoiceUpdatedV1(forkChoiceState3);
                forkchoiceUpdatedResult3.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);

                Keccak currentBlockHash = chain.BlockTree.Head!.Hash!;
                Assert.False(currentBlockHash != forkChoiceState3.HeadBlockHash ||
                             currentBlockHash == forkChoiceState3.SafeBlockHash ||
                             currentBlockHash == forkChoiceState3.FinalizedBlockHash);
            }
        }

        private async Task<BlockRequestResult> BuildAndGetPayloadResult(
            IEngineRpcModule rpc, MergeTestBlockchain chain, Keccak headBlockHash, Keccak finalizedBlockHash,
            Keccak safeBlockHash,
            UInt256 timestamp, Keccak random, Address feeRecipient, bool waitForBlockImprovement = true)
        {
            SemaphoreSlim blockImprovementLock = new(0);
            if (waitForBlockImprovement)
            {
                chain.PayloadPreparationService!.BlockImproved += (s, e) =>
                {
                    blockImprovementLock.Release(1);
                };
            }

            ForkchoiceStateV1 forkchoiceState = new(headBlockHash, finalizedBlockHash, safeBlockHash);
            PayloadAttributes payloadAttributes =
                new() { Timestamp = timestamp, PrevRandao = random, SuggestedFeeRecipient = feeRecipient };
            string payloadId = rpc.engine_forkchoiceUpdatedV1(forkchoiceState, payloadAttributes).Result.Data.PayloadId;
            if (waitForBlockImprovement)
                await blockImprovementLock.WaitAsync(10000);
            ResultWrapper<BlockRequestResult?> getPayloadResult =
                await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));
            return getPayloadResult.Data!;
        }

        private async Task<BlockRequestResult> BuildAndGetPayloadResult(MergeTestBlockchain chain,
            IEngineRpcModule rpc, PayloadAttributes payloadAttributes)
        {
            Keccak startingHead = chain.BlockTree.HeadHash;
            Keccak parentHead = chain.BlockTree.Head!.ParentHash!;

            return await BuildAndGetPayloadResult(rpc, chain, startingHead, parentHead, startingHead,
                payloadAttributes.Timestamp, payloadAttributes.PrevRandao!, payloadAttributes.SuggestedFeeRecipient);
        }

        private async Task<BlockRequestResult> BuildAndGetPayloadResult(MergeTestBlockchain chain,
            IEngineRpcModule rpc)
        {
            Keccak startingHead = chain.BlockTree.HeadHash;
            Keccak parentHead = chain.BlockTree.Head!.ParentHash!;

            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;

            return await BuildAndGetPayloadResult(rpc, chain, startingHead, parentHead, startingHead,
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
