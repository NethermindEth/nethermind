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
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;
using Result = Nethermind.Merge.Plugin.Data.Result;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Merge.Plugin.Test
{
    [Parallelizable(ParallelScope.All)]
    public partial class EngineModuleTests
    {
        private static readonly DateTime Timestamp = DateTimeOffset.FromUnixTimeSeconds(1000).UtcDateTime;
        private ITimestamper Timestamper { get; } = new ManualTimestamper(Timestamp);

        [Test, Retry(3)]
        public async Task preparePayload_should_create_block_on_top_of_genesis()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;
            uint payloadId = 111;

            await rpc.engine_preparePayload(startingHead, timestamp, random, feeRecipient, payloadId);
            ResultWrapper<BlockRequestResult?> response = await rpc.engine_getPayload(payloadId);

            BlockRequestResult expected = CreateParentBlockRequestOnHead(chain.BlockTree);
            expected.GasLimit = 4000000L;
            expected.BlockHash = new Keccak("0xdc4e882186c2723e6ed279634d6b7f502bf2712dc3113c743903786b61c55c87");
            expected.LogsBloom = Bloom.Empty;
            expected.Miner = chain.MinerAddress;
            expected.Number = 1;
            expected.ParentHash = startingHead;
            expected.SetTransactions(Array.Empty<Transaction>());
            expected.Timestamp = timestamp;
            expected.Random = random;

            response.Data.Should().BeEquivalentTo(expected);
        }

        [Test]
        [Ignore("ToDo")]
        public async Task getPayload_should_return_error_if_there_was_no_preparePayload_at_all()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            uint payloadId = 111;

            ResultWrapper<BlockRequestResult?> response = await rpc.engine_getPayload(payloadId);

            response.Data.Should().Be("some kind of error message - requested unprepared payload");
        }

        [Test]
        [Ignore("ToDo")]
        public async Task getPayload_should_return_error_if_there_was_no_corresponding_preparePayload()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;
            uint payloadId = 111;

            await rpc.engine_preparePayload(startingHead, timestamp, random, feeRecipient, payloadId);

            uint requestedPayloadId = 222;
            ResultWrapper<BlockRequestResult?> response = await rpc.engine_getPayload(requestedPayloadId);

            response.Data.Should().Be("some kind of error message - requested unprepared payload");
        }

        [Test]
        [Ignore("ToDo")]
        public async Task getPayload_should_return_error_if_called_after_timeout()
        {
            const int timeout = 5000;

            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;
            uint payloadId = 111;

            await rpc.engine_preparePayload(startingHead, timestamp, random, feeRecipient, payloadId);

            Thread.Sleep(timeout);

            ResultWrapper<BlockRequestResult?> response = await rpc.engine_getPayload(payloadId);

            response.Data.Should().Be("some kind of error message - timeout");
        }

        // ToDo need for rework
        // [Test]
        // public async Task assembleBlock_should_not_create_block_with_unknown_parent()
        // {
        //     using MergeTestBlockchain chain = await CreateBlockChain();
        //     IEngineRpcModule rpc = CreateEngineModule(chain);
        //     Keccak notExistingHash = TestItem.KeccakH;
        //     PreparePayloadRequest preparePayloadRequest = new() {ParentHash = notExistingHash};
        //     ResultWrapper<BlockRequestResult?> response = await rpc.engine_preparePayload(preparePayloadRequest);
        //     response.Data.Should().BeNull();
        // }

        [Test]
        [Ignore("ToDo")]
        public async Task executePayload_accepts_previously_assembled_block_multiple_times([Values(1, 3)] int times)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockHeader startingBestSuggestedHeader = chain.BlockTree.BestSuggestedHeader!;
            BlockRequestResult getPayloadResult = await PrepareAndGetPayloadResult(chain, rpc);
            getPayloadResult.ParentHash.Should().Be(startingHead);

            ResultWrapper<ExecutePayloadResult>
                executePayloadResult = await rpc.engine_executePayload(getPayloadResult);
            executePayloadResult.Data.Status.Should().Be(VerificationStatus.Valid);

            for (int i = 1; i < times; i++)
            {
                executePayloadResult = await rpc.engine_executePayload(getPayloadResult);
                executePayloadResult.Data.Status.Should().Be(VerificationStatus.Known);
            }

            Keccak bestSuggestedHeaderHash = chain.BlockTree.BestSuggestedHeader!.Hash!;
            bestSuggestedHeaderHash.Should().Be(getPayloadResult.BlockHash);
            bestSuggestedHeaderHash.Should().NotBe(startingBestSuggestedHeader!.Hash!);
        }

        public static IEnumerable WrongInputTests
        {
            get
            {
                yield return GetNewBlockRequestBadDataTestCase(r => r.BlockHash, TestItem.KeccakA);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Difficulty, UInt256.One);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Difficulty, 2ul);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Nonce, 1ul);
                yield return GetNewBlockRequestBadDataTestCase(r => r.MixHash, TestItem.KeccakC);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Uncles, new[] {TestItem.KeccakB});
                yield return GetNewBlockRequestBadDataTestCase(r => r.ParentHash, TestItem.KeccakD);
                yield return GetNewBlockRequestBadDataTestCase(r => r.ReceiptsRoot, TestItem.KeccakD);
                yield return GetNewBlockRequestBadDataTestCase(r => r.StateRoot, TestItem.KeccakD);

                Bloom bloom = new();
                bloom.Add(new[]
                {
                    Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakG).TestObject
                });
                yield return GetNewBlockRequestBadDataTestCase(r => r.LogsBloom, bloom);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Transactions, new byte[][] {new byte[] {1}});
                yield return GetNewBlockRequestBadDataTestCase(r => r.GasUsed, 1);
            }
        }

        [TestCaseSource(nameof(WrongInputTests))]
        public async Task executePayload_rejects_incorrect_input(Action<BlockRequestResult> breakerAction)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult getPayloadResult = await PrepareAndGetPayloadResult(chain, rpc);
            Keccak blockHash = getPayloadResult.BlockHash;
            breakerAction(getPayloadResult);
            if (blockHash == getPayloadResult.BlockHash && TryCalculateHash(getPayloadResult, out Keccak? hash))
            {
                getPayloadResult.BlockHash = hash;
            }

            ResultWrapper<ExecutePayloadResult>
                executePayloadResult = await rpc.engine_executePayload(getPayloadResult);
            executePayloadResult.Data.Status.Should().Be(VerificationStatus.Invalid);
        }

        [Test]
        public async Task executePayload_accepts_already_known_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Block block = Build.A.Block.WithNumber(1).WithParent(chain.BlockTree.Head!).TestObject;
            block.Header.Hash = new Keccak("0xdc3419cbd81455372f3e576f930560b35ec828cd6cdfbd4958499e43c68effdf");
            chain.BlockTree.SuggestBlock(block);

            ResultWrapper<ExecutePayloadResult> executePayloadResult =
                await rpc.engine_executePayload(new BlockRequestResult(block, Keccak.Zero));
            executePayloadResult.Data.Status.Should().Be(VerificationStatus.Known);
        }

        [Test]
        public async Task forkchoiceUpdated_should_work_with_zero_keccak_for_finalization()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockRequestResult blockRequestResult = await SendNewBlock(rpc, chain);

            Keccak newHeadHash = blockRequestResult.BlockHash;
            ResultWrapper<Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdated(newHeadHash!, Keccak.Zero, startingHead);
            forkchoiceUpdatedResult.Data.Should().Be(Result.Ok);

            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(startingHead);
            actualHead.Should().Be(newHeadHash);
            AssertExecutionStatusChanged(rpc, newHeadHash!, Keccak.Zero, startingHead);
        }

        [Test]
        public async Task forkchoiceUpdated_should_change_head()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockRequestResult blockRequestResult = await SendNewBlock(rpc, chain);

            Keccak newHeadHash = blockRequestResult.BlockHash;
            ResultWrapper<Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdated(newHeadHash!, startingHead, startingHead);
            forkchoiceUpdatedResult.Data.Should().Be(Result.Ok);

            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(startingHead);
            actualHead.Should().Be(newHeadHash);
            AssertExecutionStatusChanged(rpc, newHeadHash!, startingHead, startingHead);
        }

        [Test]
        public async Task forkChoiceUpdated_to_unknown_block_fails()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            ResultWrapper<Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdated(TestItem.KeccakF, TestItem.KeccakF, TestItem.KeccakF);
            forkchoiceUpdatedResult.ErrorCode.Should().Be(MergeErrorCodes.UnknownHeader);
            AssertExecutionStatusNotChanged(rpc, TestItem.KeccakF, TestItem.KeccakF, TestItem.KeccakF);
        }

        [Test]
        public async Task forkChoiceUpdated_to_unknown_confirmation_hash_should_fail()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockRequestResult blockRequestResult = await SendNewBlock(rpc, chain);

            Keccak newHeadHash = blockRequestResult.BlockHash;
            ResultWrapper<Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdated(newHeadHash!, startingHead, TestItem.KeccakF);
            forkchoiceUpdatedResult.ErrorCode.Should().Be(MergeErrorCodes.UnknownHeader);

            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(newHeadHash);
        }

        [Test]
        public async Task forkChoiceUpdated_no_common_branch_fails()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak? startingHead = chain.BlockTree.HeadHash;
            BlockHeader parent = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            Block block = Build.A.Block.WithNumber(2).WithParent(parent).TestObject;
            chain.BlockTree.SuggestBlock(block);

            ResultWrapper<Result> forkchoiceUpdatedResult =
                await rpc.engine_forkchoiceUpdated(block.Hash!, startingHead, startingHead);
            forkchoiceUpdatedResult.ErrorCode.Should().Be(MergeErrorCodes.UnknownHeader);
            AssertExecutionStatusNotChanged(rpc, block.Hash!, startingHead, startingHead);
        }

        [Test]
        public async Task forkchoiceUpdated_should_change_head_when_all_parameters_are_the_newHeadHash()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = await SendNewBlock(rpc, chain);

            Keccak newHeadHash = blockRequestResult.BlockHash;
            ResultWrapper<Result> resultWrapper =
                await rpc.engine_forkchoiceUpdated(newHeadHash, newHeadHash, newHeadHash);
            resultWrapper.Data.Should().Be(Result.Ok);
            AssertExecutionStatusChanged(rpc, newHeadHash, newHeadHash, newHeadHash);
        }

        [Test]
        public async Task forkchoiceUpdated_switch_to_pos_when_total_terminal_difficulty_was_met()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = await SendNewBlock(rpc, chain);
            Assert.False(chain.PoSSwitcher.WasEverInPoS());

            rpc.engine_terminalTotalDifficultyUpdated((UInt256)1000000);
            Keccak newHeadHash = blockRequestResult.BlockHash;
            ResultWrapper<Result> resultWrapper =
                await rpc.engine_forkchoiceUpdated(newHeadHash, newHeadHash, newHeadHash);
            resultWrapper.Data.Should().Be(Result.Ok);
            AssertExecutionStatusChanged(rpc, newHeadHash, newHeadHash, newHeadHash);
            Assert.True(chain.PoSSwitcher.WasEverInPoS());
        }

        [Test]
        public async Task forkchoiceUpdated_not_switch_to_pos_where_no_terminal_values()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = await SendNewBlock(rpc, chain);
            Assert.False(chain.PoSSwitcher.WasEverInPoS());
            Keccak newHeadHash = blockRequestResult.BlockHash;
            ResultWrapper<Result> resultWrapper =
                await rpc.engine_forkchoiceUpdated(newHeadHash, newHeadHash, newHeadHash);
            resultWrapper.Data.Should().Be(Result.Ok);
            AssertExecutionStatusChanged(rpc, newHeadHash, newHeadHash, newHeadHash);
            Assert.False(chain.PoSSwitcher.WasEverInPoS());
        }

        [Test]
        public async Task forkchoiceUpdated_switch_to_pos_by_terminal_block_hash()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = await SendNewBlock(rpc, chain);

            rpc.engine_terminalPoWBlockOverride(chain.BlockTree.HeadHash);
            Keccak newHeadHash = blockRequestResult.BlockHash;
            ResultWrapper<Result> resultWrapper =
                await rpc.engine_forkchoiceUpdated(newHeadHash, newHeadHash, newHeadHash);
            resultWrapper.Data.Should().Be(Result.Ok);
            AssertExecutionStatusChanged(rpc, newHeadHash, newHeadHash, newHeadHash);
            Assert.True(chain.PoSSwitcher.WasEverInPoS());
        }

        [Test]
        [Ignore("ToDo - it is failing because of total difficulty check in blockTree")]
        public async Task executePayload_accepts_first_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressD);
            ResultWrapper<ExecutePayloadResult> resultWrapper = await rpc.engine_executePayload(blockRequestResult);
            resultWrapper.Data.Status.Should().Be(VerificationStatus.Valid);
            new BlockRequestResult(chain.BlockTree.BestSuggestedBody, blockRequestResult.Random).Should()
                .BeEquivalentTo(blockRequestResult);
        }

        private async Task<BlockRequestResult> SendNewBlock(IEngineRpcModule rpc, MergeTestBlockchain chain)
        {
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree),
                TestItem.AddressD);
            ResultWrapper<ExecutePayloadResult> executePayloadResult = await rpc.engine_executePayload(blockRequestResult);
            ResultWrapper<Result> consensusValidatedResult =
                await rpc.engine_consensusValidated(blockRequestResult.BlockHash, ConsensusValidationStatus.Valid);
            executePayloadResult.Data.Status.Should().Be(VerificationStatus.Valid);
            consensusValidatedResult.Data.Should().Be(Result.Ok);
            return blockRequestResult;
        }

        private void AssertExecutionStatusChanged(IEngineRpcModule rpc, Keccak headBlockHash, Keccak finalizedBlockHash,
            Keccak confirmedBlockHash)
        {
            ExecutionStatusResult? result = rpc.engine_executionStatus().Data;
            Assert.AreEqual(headBlockHash, result.HeadBlockHash);
            Assert.AreEqual(finalizedBlockHash, result.FinalizedBlockHash);
            Assert.AreEqual(confirmedBlockHash, result.ConfirmedBlockHash);
        }

        private void AssertExecutionStatusNotChanged(IEngineRpcModule rpc, Keccak headBlockHash,
            Keccak finalizedBlockHash, Keccak confirmedBlockHash)
        {
            ExecutionStatusResult? result = rpc.engine_executionStatus().Data;
            Assert.AreNotEqual(headBlockHash, result.HeadBlockHash);
            Assert.AreNotEqual(finalizedBlockHash, result.FinalizedBlockHash);
            Assert.AreNotEqual(confirmedBlockHash, result.ConfirmedBlockHash);
        }

        [TestCase(30)]
        public async Task can_progress_chain_one_by_one(int count)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak lastHash = (await ProduceBranch(rpc, chain.BlockTree, count, chain.BlockTree.HeadHash, true)).Last()
                .BlockHash;
            chain.BlockTree.HeadHash.Should().Be(lastHash);
            Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, b => b.IsGenesis, true);
            last.Should().NotBeNull();
            last!.IsGenesis.Should().BeTrue();
        }

        [Test]
        public async Task forkchoiceUpdated_can_reorganize_to_any_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);

            async Task CanReorganizeToBlock(BlockRequestResult block, MergeTestBlockchain testChain)
            {
                ResultWrapper<Result> result =
                    await rpc.engine_forkchoiceUpdated(block.BlockHash, block.BlockHash, block.BlockHash);
                result.Data.Should().Be(Result.Ok);
                testChain.BlockTree.HeadHash.Should().Be(block.BlockHash);
                testChain.BlockTree.Head!.Number.Should().Be(block.Number);
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
                await ProduceBranch(rpc, chain.BlockTree, 10, chain.BlockTree.HeadHash, false);
            IReadOnlyList<BlockRequestResult> branch2 =
                await ProduceBranch(rpc, chain.BlockTree, 5, branch1[3].BlockHash, false, 2);
            branch2.Last().Number.Should().Be(1 + 3 + 5);
            IReadOnlyList<BlockRequestResult> branch3 =
                await ProduceBranch(rpc, chain.BlockTree, 7, branch1[7].BlockHash, false, 3);
            branch3.Last().Number.Should().Be(1 + 7 + 7);
            IReadOnlyList<BlockRequestResult> branch4 =
                await ProduceBranch(rpc, chain.BlockTree, 3, branch3[4].BlockHash, false, 4);
            branch3.Last().Number.Should().Be(1 + 7 + 4 + 3);

            await CanReorganizeToAnyBlock(chain, branch1, branch2, branch3, branch4);
        }

        [Test]
        public async Task assembleBlock_can_build_on_any_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            ulong payloadId = 500;

            async Task CanAssembleOnBlock(BlockRequestResult block)
            {
                UInt256 timestamp = Timestamper.UnixTime.Seconds;
                Keccak random = Keccak.Zero;
                Address feeRecipient = Address.Zero;
                await rpc.engine_preparePayload(block.BlockHash, timestamp, random, feeRecipient, payloadId);
                ResultWrapper<BlockRequestResult?>? response = await rpc.engine_getPayload(payloadId);

                response.Data.Should().NotBeNull();
                response.Data!.ParentHash.Should().Be(block.BlockHash);
                ++payloadId;
            }

            async Task CanAssembleOnAnyBlock(params IReadOnlyList<BlockRequestResult>[] branches)
            {
                foreach (IReadOnlyList<BlockRequestResult>? branch in branches)
                {
                    await CanAssembleOnBlock(branch.Last());
                }

                foreach (IReadOnlyList<BlockRequestResult>? branch in branches)
                {
                    foreach (BlockRequestResult block in branch)
                    {
                        await CanAssembleOnBlock(block);
                    }

                    foreach (BlockRequestResult block in branch.Reverse())
                    {
                        await CanAssembleOnBlock(block);
                    }
                }
            }

            IReadOnlyList<BlockRequestResult> branch1 =
                await ProduceBranch(rpc, chain.BlockTree, 10, chain.BlockTree.HeadHash, false);
            IReadOnlyList<BlockRequestResult> branch2 =
                await ProduceBranch(rpc, chain.BlockTree, 5, branch1[3].BlockHash, false, 2);
            branch2.Last().Number.Should().Be(1 + 3 + 5);
            IReadOnlyList<BlockRequestResult> branch3 =
                await ProduceBranch(rpc, chain.BlockTree, 7, branch1[7].BlockHash, false, 3);
            branch3.Last().Number.Should().Be(1 + 7 + 7);
            IReadOnlyList<BlockRequestResult> branch4 =
                await ProduceBranch(rpc, chain.BlockTree, 3, branch3[4].BlockHash, false, 4);
            branch3.Last().Number.Should().Be(1 + 7 + 4 + 3);

            await CanAssembleOnAnyBlock(branch1, branch2, branch3, branch4);
        }

        [Test]
        // [Repeat(1000)] // to test multi-thread issue, warning - long and eliminated in test already
        public async Task executePayload_processes_passed_transactions([Values(false, true)] bool moveHead)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            IReadOnlyList<BlockRequestResult> branch =
                await ProduceBranch(rpc, chain.BlockTree, 10, chain.BlockTree.HeadHash, moveHead);

            foreach (BlockRequestResult block in branch)
            {
                uint count = 10;
                BlockRequestResult newBlockRequest = CreateBlockRequest(block, TestItem.AddressA);
                PrivateKey from = TestItem.PrivateKeyB;
                Address to = TestItem.AddressD;
                (_, UInt256 toBalanceAfter) = AddTransactions(chain, newBlockRequest, from, to, count, 1,
                    out BlockHeader? parentHeader);

                newBlockRequest.GasUsed = GasCostOf.Transaction * count;
                newBlockRequest.StateRoot =
                    new Keccak("0x3d2e3ced6da0d1e94e65894dc091190480f045647610ef614e1cab4241ca66e0");
                newBlockRequest.ReceiptsRoot =
                    new Keccak("0xc538d36ed1acf6c28187110a2de3e5df707d6d38982f436eb0db7a623f9dc2cd");
                TryCalculateHash(newBlockRequest, out Keccak? hash);
                newBlockRequest.BlockHash = hash;
                ResultWrapper<ExecutePayloadResult> result = await rpc.engine_executePayload(newBlockRequest);
                await Task.Delay(10);

                result.Data.Status.Should().Be(VerificationStatus.Valid);
                RootCheckVisitor rootCheckVisitor = new();
                chain.StateReader.RunTreeVisitor(rootCheckVisitor, newBlockRequest.StateRoot);
                rootCheckVisitor.HasRoot.Should().BeTrue();
                // Chain.StateReader.GetBalance(newBlockRequest.StateRoot, from.Address).Should().Be(fromBalanceAfter);
                chain.StateReader.GetBalance(newBlockRequest.StateRoot, to).Should().Be(toBalanceAfter);
                if (moveHead)
                {
                    await rpc.engine_forkchoiceUpdated(newBlockRequest.BlockHash, newBlockRequest.BlockHash,
                        newBlockRequest.BlockHash);
                    await Task.Delay(10);
                    chain.State.StateRoot.Should().Be(newBlockRequest.StateRoot);
                    chain.State.StateRoot.Should().NotBe(parentHeader.StateRoot!);
                }
            }
        }

        [Test]
        public async Task executePayload_transactions_produce_receipts()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            IReadOnlyList<BlockRequestResult> branch =
                await ProduceBranch(rpc, chain.BlockTree, 1, chain.BlockTree.HeadHash, false);

            foreach (BlockRequestResult block in branch)
            {
                uint count = 10;
                BlockRequestResult newBlockRequest = CreateBlockRequest(block, TestItem.AddressA);
                PrivateKey from = TestItem.PrivateKeyB;
                Address to = TestItem.AddressD;
                var (_, toBalanceAfter) =
                    AddTransactions(chain, newBlockRequest, from, to, count, 1, out var parentHeader);

                newBlockRequest.GasUsed = GasCostOf.Transaction * count;
                newBlockRequest.StateRoot =
                    new Keccak("0x3d2e3ced6da0d1e94e65894dc091190480f045647610ef614e1cab4241ca66e0");
                newBlockRequest.ReceiptsRoot =
                    new Keccak("0xc538d36ed1acf6c28187110a2de3e5df707d6d38982f436eb0db7a623f9dc2cd");
                TryCalculateHash(newBlockRequest, out var hash);
                newBlockRequest.BlockHash = hash;
                ResultWrapper<ExecutePayloadResult> result = await rpc.engine_executePayload(newBlockRequest);
                await Task.Delay(10);

                result.Data.Status.Should().Be(VerificationStatus.Valid);
                RootCheckVisitor rootCheckVisitor = new();
                chain.StateReader.RunTreeVisitor(rootCheckVisitor, newBlockRequest.StateRoot);
                rootCheckVisitor.HasRoot.Should().BeTrue();
                // Chain.StateReader.GetBalance(newBlockRequest.StateRoot, from.Address).Should().Be(fromBalanceAfter);
                chain.StateReader.GetBalance(newBlockRequest.StateRoot, to).Should().Be(toBalanceAfter);
                Block findBlock = chain.BlockTree.FindBlock(newBlockRequest.BlockHash, BlockTreeLookupOptions.None)!;
                TxReceipt[]? receipts = chain.ReceiptStorage.Get(findBlock);
                findBlock.Transactions.Select(t => t.Hash).Should().BeEquivalentTo(receipts.Select(r => r.TxHash));
            }
        }

        [Test]
        public async Task assembleBlock_picks_transactions_from_pool()
        {
            SemaphoreSlim semaphoreSlim = new SemaphoreSlim(0);
            uint payloadId = 1;
            ManualTimestamper timestamper = new(Timestamp);
            using MergeTestBlockchain chain = await CreateBlockChain();
            IEngineRpcModule rpc = CreateEngineModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            uint count = 3;
            int value = 10;
            Address recipient = TestItem.AddressD;
            PrivateKey sender = TestItem.PrivateKeyB;
            Transaction[] transactions =
                BuildTransactions(chain, startingHead, sender, recipient, count, value, out _, out _);
            chain.AddTransactions(transactions);
            chain.BlockProducer.BlockProduced += (s, e) =>
            {
                semaphoreSlim.Release(1);
            };
            await rpc.engine_preparePayload(startingHead, ((ITimestamper)timestamper).UnixTime.Seconds,
                TestItem.KeccakA, Address.Zero, payloadId);
            await semaphoreSlim.WaitAsync(-1);
            BlockRequestResult assembleBlockResult = (await rpc.engine_getPayload(payloadId)).Data!;

            assembleBlockResult.StateRoot.Should().NotBe(chain.BlockTree.Genesis!.StateRoot!);

            Transaction[] transactionsInBlock = assembleBlockResult.GetTransactions();
            transactionsInBlock.Should().BeEquivalentTo(transactions,
                o => o.Excluding(t => t.ChainId)
                    .Excluding(t => t.SenderAddress)
                    .Excluding(t => t.Timestamp)
                    .Excluding(t => t.PoolIndex)
                    .Excluding(t => t.GasBottleneck));

            await rpc.engine_consensusValidated(assembleBlockResult.BlockHash, ConsensusValidationStatus.Valid);
            ResultWrapper<ExecutePayloadResult> executePayloadResult =
                await rpc.engine_executePayload(assembleBlockResult);
            executePayloadResult.Data.Status.Should().Be(VerificationStatus.Valid);

            UInt256 totalValue = ((int)(count * value)).GWei();
            chain.StateReader.GetBalance(assembleBlockResult.StateRoot, recipient).Should().Be(totalValue);
        }

        private (UInt256, UInt256) AddTransactions(MergeTestBlockchain chain, BlockRequestResult newBlockRequest,
            PrivateKey from, Address to, uint count, int value, out BlockHeader parentHeader)
        {
            Transaction[] transactions = BuildTransactions(chain, newBlockRequest.ParentHash, from, to, count, value,
                out Account accountFrom, out parentHeader);
            newBlockRequest.SetTransactions(transactions);
            UInt256 totalValue = ((int)(count * value)).GWei();
            return (accountFrom.Balance - totalValue,
                chain.StateReader.GetBalance(parentHeader.StateRoot!, to) + totalValue);
        }

        private Transaction[] BuildTransactions(MergeTestBlockchain chain, Keccak parentHash, PrivateKey from,
            Address to, uint count, int value, out Account accountFrom, out BlockHeader parentHeader)
        {
            Transaction BuildTransaction(uint index, Account senderAccount) =>
                Build.A.Transaction.WithNonce(senderAccount.Nonce + index)
                    .WithTimestamp(Timestamper.UnixTime.Seconds)
                    .WithTo(to)
                    .WithValue(value.GWei())
                    .WithGasPrice(1.GWei())
                    .WithChainId(chain.SpecProvider.ChainId)
                    .WithSenderAddress(from.Address)
                    .SignedAndResolved(from)
                    .TestObject;

            parentHeader = chain.BlockTree.FindHeader(parentHash, BlockTreeLookupOptions.None)!;
            Account account = chain.StateReader.GetAccount(parentHeader.StateRoot!, @from.Address)!;
            accountFrom = account;

            return Enumerable.Range(0, (int)count)
                .Select(i => BuildTransaction((uint)i, account)).ToArray();
        }

        private BlockRequestResult CreateParentBlockRequestOnHead(IBlockTree blockTree)
        {
            Block? head = blockTree.Head;
            if (head == null) throw new NotSupportedException();
            return new BlockRequestResult(true)
            {
                Number = 0, BlockHash = head.Hash!, StateRoot = head.StateRoot!, ReceiptsRoot = head.ReceiptsRoot!
            };
        }

        private static BlockRequestResult CreateBlockRequest(BlockRequestResult parent, Address miner)
        {
            BlockRequestResult blockRequest = new(true)
            {
                ParentHash = parent.BlockHash,
                Miner = miner,
                StateRoot = parent.StateRoot,
                Number = parent.Number + 1,
                GasLimit = 1_000_000,
                GasUsed = 0,
                ReceiptsRoot = Keccak.EmptyTreeHash,
                LogsBloom = Bloom.Empty
            };

            blockRequest.SetTransactions(Array.Empty<Transaction>());
            TryCalculateHash(blockRequest, out Keccak? hash);
            blockRequest.BlockHash = hash;
            return blockRequest;
        }

        private async Task<IReadOnlyList<BlockRequestResult>> ProduceBranch(IEngineRpcModule rpc, IBlockTree blockTree,
            int count, Keccak parentBlockHash, bool setHead, ulong payloadIdOffset = 1)
        {
            List<BlockRequestResult> blocks = new();
            ManualTimestamper timestamper = new(Timestamp);
            for (int i = 0; i < count; i++)
            {
                ulong payloadId = payloadIdOffset * 1000 + (ulong)i;
                await rpc.engine_preparePayload(parentBlockHash, ((ITimestamper)timestamper).UnixTime.Seconds,
                    TestItem.KeccakA, Address.Zero, payloadId);
                BlockRequestResult getPayloadResult = (await rpc.engine_getPayload(payloadId)).Data!;
                Keccak? blockHash = getPayloadResult.BlockHash;
                await rpc.engine_consensusValidated(blockHash, ConsensusValidationStatus.Valid);
                ExecutePayloadResult executePayloadResponse = (await rpc.engine_executePayload(getPayloadResult)).Data;
                executePayloadResponse.Status.Should().NotBe(VerificationStatus.Invalid);
                if (setHead)
                {
                    Keccak newHead = getPayloadResult!.BlockHash;
                    ResultWrapper<Result> setHeadResponse =
                        await rpc.engine_forkchoiceUpdated(newHead, newHead, newHead);
                    setHeadResponse.Data.Should().Be(Result.Ok);
                    blockTree.HeadHash.Should().Be(newHead);
                }

                blocks.Add((getPayloadResult));
                parentBlockHash = getPayloadResult.BlockHash;
                timestamper.Add(TimeSpan.FromSeconds(12));
            }

            return blocks;
        }

        private Block? RunForAllBlocksInBranch(IBlockTree blockTree, Keccak blockHash, Func<Block, bool> shouldStop,
            bool requireCanonical)
        {
            BlockTreeLookupOptions options =
                requireCanonical ? BlockTreeLookupOptions.RequireCanonical : BlockTreeLookupOptions.None;
            Block? current = blockTree.FindBlock(blockHash, options);
            while (current is not null && !shouldStop(current))
            {
                current = blockTree.FindParent(current, options);
            }

            return current;
        }

        private async Task<BlockRequestResult> PrepareAndGetPayloadResult(MergeTestBlockchain chain,
            IEngineRpcModule rpc)
        {
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            Keccak random = Keccak.Zero;
            Address feeRecipient = Address.Zero;
            uint payloadId = 111;

            await rpc.engine_preparePayload(startingHead, timestamp, random, feeRecipient, payloadId);
            ResultWrapper<BlockRequestResult?> getPayloadResult = await rpc.engine_getPayload(payloadId);
            return getPayloadResult.Data!;
        }

        private static TestCaseData GetNewBlockRequestBadDataTestCase<T>(
            Expression<Func<BlockRequestResult, T>> propertyAccess, T wrongValue)
        {
            Action<BlockRequestResult, T> setter = propertyAccess.GetSetter();
            // ReSharper disable once ConvertToLocalFunction
            Action<BlockRequestResult> wrongValueSetter = r => setter(r, wrongValue);
            return new TestCaseData(wrongValueSetter)
            {
                TestName =
                    $"newBlock_rejects_incorrect_{propertyAccess.GetName().ToLower()}({wrongValue?.ToString()})"
            };
        }

        private static bool TryCalculateHash(BlockRequestResult request, out Keccak hash)
        {
            if (request.TryGetBlock(out Block? block) && block != null)
            {
                hash = block.CalculateHash();
                return true;
            }
            else
            {
                hash = Keccak.Zero;
                return false;
            }
        }
    }
}
