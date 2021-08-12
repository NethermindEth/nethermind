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
    public partial class ConsensusModuleTests
    {
        private static readonly DateTime Timestamp = DateTimeOffset.FromUnixTimeSeconds(1000).UtcDateTime;
        private ITimestamper Timestamper { get; } = new ManualTimestamper(Timestamp);

        [Test]
        public async Task assembleBlock_should_create_block_on_top_of_genesis()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            UInt256 timestamp = Timestamper.UnixTime.Seconds;
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = startingHead, Timestamp = timestamp};
            ResultWrapper<BlockRequestResult?> response = await rpc.consensus_assembleBlock(assembleBlockRequest);

            BlockRequestResult expected = CreateParentBlockRequestOnHead(chain.BlockTree);
            expected.GasLimit = 4000000L;
            expected.BlockHash = new Keccak("0xfe37027d377e75ffb161f11733d8880083378fe6236270c7a2ee1fc7efe71cfd");
            expected.LogsBloom = Bloom.Empty;
            expected.Miner = chain.MinerAddress;
            expected.Number = 1;
            expected.ParentHash = startingHead;
            expected.SetTransactions(Array.Empty<Transaction>());
            expected.Timestamp = timestamp;
            
            response.Data.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public async Task assembleBlock_should_not_create_block_with_unknown_parent()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            Keccak notExistingHash = TestItem.KeccakH;
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = notExistingHash};
            ResultWrapper<BlockRequestResult?> response = await rpc.consensus_assembleBlock(assembleBlockRequest);
            response.Data.Should().BeNull();
        }
        
        [Test]
        public async Task newBlock_accepts_previously_assembled_block_multiple_times([Values(1, 3)] int times)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            BlockHeader startingBestSuggestedHeader = chain.BlockTree.BestSuggestedHeader!;
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = startingHead};
            ResultWrapper<BlockRequestResult?> assembleBlockResult = await rpc.consensus_assembleBlock(assembleBlockRequest);
            assembleBlockResult.Data!.ParentHash.Should().Be(startingHead);

            for (int i = 0; i < times; i++)
            {
                ResultWrapper<NewBlockResult> newBlockResult = await rpc.consensus_newBlock(assembleBlockResult.Data!);
                newBlockResult.Data.Valid.Should().BeTrue();
            }

            Keccak bestSuggestedHeaderHash = chain.BlockTree.BestSuggestedHeader!.Hash!;
            bestSuggestedHeaderHash.Should().Be(assembleBlockResult.Data!.BlockHash);
            bestSuggestedHeaderHash.Should().NotBe(startingBestSuggestedHeader!.Hash!);
        }

        public static IEnumerable WrongInputTests
        {
            get
            {
                yield return GetNewBlockRequestBadDataTestCase(r => r.BlockHash, TestItem.KeccakA);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Difficulty, UInt256.Zero);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Difficulty, 2ul);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Nonce, 1ul);
                yield return GetNewBlockRequestBadDataTestCase(r => r.ExtraData, new byte[] {1});
                yield return GetNewBlockRequestBadDataTestCase(r => r.MixHash, TestItem.KeccakC);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Uncles, new Keccak[] {TestItem.KeccakB});
                yield return GetNewBlockRequestBadDataTestCase(r => r.ParentHash, TestItem.KeccakD);
                yield return GetNewBlockRequestBadDataTestCase(r => r.ReceiptsRoot, TestItem.KeccakD);
                yield return GetNewBlockRequestBadDataTestCase(r => r.StateRoot, TestItem.KeccakD);
                
                Bloom bloom = new();
                bloom.Add(new[] {Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakG).TestObject});
                yield return GetNewBlockRequestBadDataTestCase(r => r.LogsBloom, bloom);
                yield return GetNewBlockRequestBadDataTestCase(r => r.Transactions, new byte[][] {new byte[] {1}});
                yield return GetNewBlockRequestBadDataTestCase(r => r.GasUsed, 1);
            }
        }
        
        [TestCaseSource(nameof(WrongInputTests))]
        public async Task newBlock_rejects_incorrect_input(Action<BlockRequestResult> breakerAction)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            BlockRequestResult assembleBlockResult = await GetAssembleBlockResult(chain, rpc);
            Keccak blockHash = assembleBlockResult.BlockHash;
            breakerAction(assembleBlockResult);
            if (blockHash == assembleBlockResult.BlockHash && TryCalculateHash(assembleBlockResult, out var hash))
            {
                assembleBlockResult.BlockHash = hash;
            }
            
            ResultWrapper<NewBlockResult> newBlockResult = await rpc.consensus_newBlock(assembleBlockResult);
            newBlockResult.Data.Valid.Should().BeFalse();
        }

        [Test]
        public async Task newBlock_accepts_already_known_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            Block block = Build.A.Block.WithNumber(1).WithParent(chain.BlockTree.Head).TestObject;
            block.Header.Hash = new Keccak("0xdc3419cbd81455372f3e576f930560b35ec828cd6cdfbd4958499e43c68effdf");
            chain.BlockTree.SuggestBlock(block);
            
            ResultWrapper<NewBlockResult> newBlockResult = await rpc.consensus_newBlock(new BlockRequestResult(block));
            newBlockResult.Data.Valid.Should().BeTrue();
        } 

        [Test]
        public async Task setHead_should_change_head()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;

            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree), 
                TestItem.AddressD);
            ResultWrapper<NewBlockResult> newBlockResult = await rpc.consensus_newBlock(blockRequestResult);
            newBlockResult.Data.Valid.Should().BeTrue();
            
            Keccak newHeadHash = blockRequestResult.BlockHash;
            ResultWrapper<Result> setHeadResult = await rpc.consensus_setHead(newHeadHash!);
            setHeadResult.Data.Should().Be(Result.Ok);
            
            Keccak actualHead = chain.BlockTree.HeadHash;
            actualHead.Should().NotBe(startingHead);
            actualHead.Should().Be(newHeadHash); 
        }

        [Test]
        public async Task setHead_to_unknown_block_fails()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            ResultWrapper<Result> setHeadResult = await rpc.consensus_setHead(TestItem.KeccakF);
            setHeadResult.Data.Success.Should().BeFalse();
        }
        
        [Test]
        public async Task setHead_no_common_branch_fails()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            BlockHeader parent = Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject;
            Block block = Build.A.Block.WithNumber(2).WithParent(parent).TestObject;
            chain.BlockTree.SuggestBlock(block);
            
            ResultWrapper<Result> setHeadResult = await rpc.consensus_setHead(block.Hash!);
            setHeadResult.Data.Success.Should().BeFalse();
        }

        [Test]
        public async Task finaliseBlock_should_succeed()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            Block block = Build.A.Block.WithParent(chain.BlockTree.Head).TestObject;
            chain.BlockTree.SuggestBlock(block);
            ResultWrapper<Result> resultWrapper = await rpc.consensus_finaliseBlock(block.Hash!);
            resultWrapper.Data.Should().Be(Result.Ok);
        }
        
        [Test]
        public async Task newBlock_accepts_first_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(chain.BlockTree), 
                TestItem.AddressD);
            ResultWrapper<NewBlockResult> resultWrapper = await rpc.consensus_newBlock(blockRequestResult);
            resultWrapper.Data.Valid.Should().BeTrue();
            new BlockRequestResult(chain.BlockTree.BestSuggestedBody).Should().BeEquivalentTo(blockRequestResult);
        }

        [TestCase(30)]
        public async Task can_progress_chain_one_by_one(int count)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            Keccak lastHash = (await ProduceBranch(rpc, chain.BlockTree, count, chain.BlockTree.HeadHash, true)).Last().BlockHash;
            chain.BlockTree.HeadHash.Should().Be(lastHash);
            Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, b => b.IsGenesis, true);
            last.Should().NotBeNull();
            last!.IsGenesis.Should().BeTrue();
        }

        [Test]
        public async Task setHead_can_reorganize_to_any_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            
            async Task CanReorganizeToBlock(BlockRequestResult block, MergeTestBlockchain testChain)
            {
                ResultWrapper<Result> result = await rpc.consensus_setHead(block.BlockHash);
                result.Data.Should().Be(Result.Ok);
                testChain.BlockTree.HeadHash.Should().Be(block.BlockHash);
                testChain.BlockTree.Head!.Number.Should().Be(block.Number);
                testChain.State.StateRoot.Should().Be(testChain.BlockTree.Head!.StateRoot!);
            }
            
            async Task CanReorganizeToAnyBlock(MergeTestBlockchain testChain, params IReadOnlyList<BlockRequestResult>[] branches)
            {
                foreach (var branch in branches)
                {
                    await CanReorganizeToBlock(branch.Last(), testChain);
                }
                
                foreach (var branch in branches)
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
            
            IReadOnlyList<BlockRequestResult> branch1 = await ProduceBranch(rpc, chain.BlockTree, 10, chain.BlockTree.HeadHash, false);
            IReadOnlyList<BlockRequestResult> branch2 = await ProduceBranch(rpc, chain.BlockTree, 5, branch1[3].BlockHash, false);
            branch2.Last().Number.Should().Be(1 + 3 + 5);
            IReadOnlyList<BlockRequestResult> branch3 = await ProduceBranch(rpc, chain.BlockTree, 7, branch1[7].BlockHash, false);
            branch3.Last().Number.Should().Be(1 + 7 + 7);
            IReadOnlyList<BlockRequestResult> branch4 = await ProduceBranch(rpc, chain.BlockTree, 3, branch3[4].BlockHash, false);
            branch3.Last().Number.Should().Be(1 + 7 + 4 + 3);

            await CanReorganizeToAnyBlock(chain, branch1, branch2, branch3, branch4);
        }

        [Test]
        public async Task assembleBlock_can_build_on_any_block()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            
            async Task CanAssembleOnBlock(BlockRequestResult block)
            {
                UInt256 timestamp = Timestamper.UnixTime.Seconds;
                AssembleBlockRequest assembleBlockRequest = new() {ParentHash = block.BlockHash, Timestamp = timestamp};
                ResultWrapper<BlockRequestResult?> response = await rpc.consensus_assembleBlock(assembleBlockRequest);

                response.Data.Should().NotBeNull();
                response.Data!.ParentHash.Should().Be(block.BlockHash);
            }
            
            async Task CanAssembleOnAnyBlock(params IReadOnlyList<BlockRequestResult>[] branches)
            {
                foreach (var branch in branches)
                {
                    await CanAssembleOnBlock(branch.Last());
                }
                
                foreach (var branch in branches)
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

            IReadOnlyList<BlockRequestResult> branch1 = await ProduceBranch(rpc, chain.BlockTree, 10, chain.BlockTree.HeadHash, false);
            IReadOnlyList<BlockRequestResult> branch2 = await ProduceBranch(rpc, chain.BlockTree, 5, branch1[3].BlockHash, false);
            branch2.Last().Number.Should().Be(1 + 3 + 5);
            IReadOnlyList<BlockRequestResult> branch3 = await ProduceBranch(rpc, chain.BlockTree, 7, branch1[7].BlockHash, false);
            branch3.Last().Number.Should().Be(1 + 7 + 7);
            IReadOnlyList<BlockRequestResult> branch4 = await ProduceBranch(rpc, chain.BlockTree, 3, branch3[4].BlockHash, false);
            branch3.Last().Number.Should().Be(1 + 7 + 4 + 3);
            
            await CanAssembleOnAnyBlock(branch1, branch2, branch3, branch4);
        }
        
        [Test]
        // [Repeat(1000)] // to test multi-thread issue, warning - long and eliminated in test already
        public async Task newBlock_processes_passed_transactions([Values(false, true)] bool moveHead)
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            IReadOnlyList<BlockRequestResult> branch = await ProduceBranch(rpc, chain.BlockTree, 10, chain.BlockTree.HeadHash, moveHead);

            foreach (BlockRequestResult block in branch)
            {
                uint count = 10;
                BlockRequestResult newBlockRequest = CreateBlockRequest(block, TestItem.AddressA);
                PrivateKey from = TestItem.PrivateKeyB;
                Address to = TestItem.AddressD;
                var (_, toBalanceAfter) = AddTransactions(chain, newBlockRequest, from, to, count, 1, out var parentHeader);

                newBlockRequest.GasUsed = GasCostOf.Transaction * count;
                newBlockRequest.StateRoot = new Keccak("0x3d2e3ced6da0d1e94e65894dc091190480f045647610ef614e1cab4241ca66e0");
                newBlockRequest.ReceiptsRoot = new Keccak("0xc538d36ed1acf6c28187110a2de3e5df707d6d38982f436eb0db7a623f9dc2cd");
                TryCalculateHash(newBlockRequest, out var hash);
                newBlockRequest.BlockHash = hash;
                ResultWrapper<NewBlockResult> result = await rpc.consensus_newBlock(newBlockRequest);
                await Task.Delay(10);

                result.Data.Valid.Should().BeTrue();
                RootCheckVisitor rootCheckVisitor = new();
                chain.StateReader.RunTreeVisitor(rootCheckVisitor, newBlockRequest.StateRoot);
                rootCheckVisitor.HasRoot.Should().BeTrue();
                // Chain.StateReader.GetBalance(newBlockRequest.StateRoot, from.Address).Should().Be(fromBalanceAfter);
                chain.StateReader.GetBalance(newBlockRequest.StateRoot, to).Should().Be(toBalanceAfter);
                if (moveHead)
                {
                    await rpc.consensus_setHead(newBlockRequest.BlockHash);
                    await Task.Delay(10);
                    chain.State.StateRoot.Should().Be(newBlockRequest.StateRoot);
                    chain.State.StateRoot.Should().NotBe(parentHeader.StateRoot!);
                }
            }
        }
        
        [Test]
        public async Task newBlock_transactions_produce_receipts()
        {
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            IReadOnlyList<BlockRequestResult> branch = await ProduceBranch(rpc, chain.BlockTree, 1, chain.BlockTree.HeadHash, false);

            foreach (BlockRequestResult block in branch)
            {
                uint count = 10;
                BlockRequestResult newBlockRequest = CreateBlockRequest(block, TestItem.AddressA);
                PrivateKey from = TestItem.PrivateKeyB;
                Address to = TestItem.AddressD;
                var (_, toBalanceAfter) = AddTransactions(chain, newBlockRequest, from, to, count, 1, out var parentHeader);

                newBlockRequest.GasUsed = GasCostOf.Transaction * count;
                newBlockRequest.StateRoot = new Keccak("0x3d2e3ced6da0d1e94e65894dc091190480f045647610ef614e1cab4241ca66e0");
                newBlockRequest.ReceiptsRoot = new Keccak("0xc538d36ed1acf6c28187110a2de3e5df707d6d38982f436eb0db7a623f9dc2cd");
                TryCalculateHash(newBlockRequest, out var hash);
                newBlockRequest.BlockHash = hash;
                ResultWrapper<NewBlockResult> result = await rpc.consensus_newBlock(newBlockRequest);
                await Task.Delay(10);

                result.Data.Valid.Should().BeTrue();
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
            using MergeTestBlockchain chain = await CreateBlockChain();
            IConsensusRpcModule rpc = CreateConsensusModule(chain);
            Keccak startingHead = chain.BlockTree.HeadHash;
            uint count = 3;
            int value = 10;
            Address recipient = TestItem.AddressD;
            PrivateKey sender = TestItem.PrivateKeyB;
            Transaction[] transactions = BuildTransactions(chain, startingHead, sender, recipient, count, value, out _, out _);
            chain.AddTransactions(transactions);
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = startingHead};
            BlockRequestResult assembleBlockResult = (await rpc.consensus_assembleBlock(assembleBlockRequest)).Data!;

            assembleBlockResult.StateRoot.Should().NotBe(chain.BlockTree.Genesis!.StateRoot!);
            
            Transaction[] transactionsInBlock = assembleBlockResult.GetTransactions();
            transactionsInBlock.Should().BeEquivalentTo(transactions, 
                o => o.Excluding(t => t.ChainId)
                    .Excluding(t => t.SenderAddress)
                    .Excluding(t => t.Timestamp)
                    .Excluding(t => t.PoolIndex)
                    .Excluding(t => t.GasBottleneck));

            ResultWrapper<NewBlockResult> newBlockResult = await rpc.consensus_newBlock(assembleBlockResult);
            newBlockResult.Data.Valid.Should().BeTrue();

            UInt256 totalValue = ((int)(count * value)).GWei();
            chain.StateReader.GetBalance(assembleBlockResult.StateRoot, recipient).Should().Be(totalValue);
        }
        
        private (UInt256, UInt256) AddTransactions(MergeTestBlockchain chain, BlockRequestResult newBlockRequest, PrivateKey from, Address to, uint count, int value, out BlockHeader parentHeader)
        {
            Transaction[] transactions = BuildTransactions(chain, newBlockRequest.ParentHash, from, to, count, value, out Account accountFrom, out parentHeader);
            newBlockRequest.SetTransactions(transactions);
            UInt256 totalValue = ((int)(count * value)).GWei();
            return (accountFrom.Balance - totalValue, chain.StateReader.GetBalance(parentHeader.StateRoot!, to) + totalValue);
        }

        private Transaction[] BuildTransactions(MergeTestBlockchain chain, Keccak parentHash, PrivateKey from, Address to, uint count, int value, out Account accountFrom, out BlockHeader parentHeader)
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
            return new BlockRequestResult(true) {Number = 0, BlockHash = head.Hash!, StateRoot = head.StateRoot!, ReceiptsRoot = head.ReceiptsRoot!};
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
            TryCalculateHash(blockRequest, out var hash);
            blockRequest.BlockHash = hash;
            return blockRequest;
        }
        
        private async Task<IReadOnlyList<BlockRequestResult>> ProduceBranch(IConsensusRpcModule rpc, IBlockTree blockTree, int count, Keccak parentBlockHash, bool setHead)
        {
            List<BlockRequestResult> blocks = new();
            ManualTimestamper timestamper = new(Timestamp);
            for (int i = 0; i < count; i++)
            {
                AssembleBlockRequest assembleBlockRequest = new() {ParentHash = parentBlockHash, Timestamp = ((ITimestamper) timestamper).UnixTime.Seconds};
                BlockRequestResult assembleBlockResponse = (await rpc.consensus_assembleBlock(assembleBlockRequest)).Data!;
                NewBlockResult newBlockResponse = (await rpc.consensus_newBlock(assembleBlockResponse!)).Data;
                newBlockResponse.Valid.Should().BeTrue();
                if (setHead)
                {
                    Keccak newHead = assembleBlockResponse.BlockHash;
                    ResultWrapper<Result> setHeadResponse = await rpc.consensus_setHead(newHead);
                    setHeadResponse.Data.Should().Be(Result.Ok);
                    blockTree.HeadHash.Should().Be(newHead);
                }
                blocks.Add((assembleBlockResponse));
                parentBlockHash = assembleBlockResponse.BlockHash;
                timestamper.Add(TimeSpan.FromSeconds(12));
            }

            return blocks;
        }
        
        private Block? RunForAllBlocksInBranch(IBlockTree blockTree, Keccak blockHash, Func<Block, bool> shouldStop, bool requireCanonical)
        {
            var options = requireCanonical ? BlockTreeLookupOptions.RequireCanonical : BlockTreeLookupOptions.None;
            Block? current = blockTree.FindBlock(blockHash, options);
            while (current is not null && !shouldStop(current))
            {
                current = blockTree.FindParent(current, options);
            }

            return current;
        }
        
        private static async Task<BlockRequestResult> GetAssembleBlockResult(MergeTestBlockchain chain, IConsensusRpcModule rpc)
        {
            Keccak startingHead = chain.BlockTree.HeadHash;
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = startingHead};
            ResultWrapper<BlockRequestResult?> assembleBlockResult = await rpc.consensus_assembleBlock(assembleBlockRequest);
            return assembleBlockResult.Data!;
        }
        
        private static TestCaseData GetNewBlockRequestBadDataTestCase<T>(Expression<Func<BlockRequestResult, T>> propertyAccess, T wrongValue)
        {
            Action<BlockRequestResult,T> setter = propertyAccess.GetSetter();
            // ReSharper disable once ConvertToLocalFunction
            Action<BlockRequestResult> wrongValueSetter = r => setter(r, wrongValue);
            return new TestCaseData(wrongValueSetter) {TestName = $"newBlock_rejects_incorrect_{propertyAccess.GetName().ToLower()}({wrongValue?.ToString()})"};
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
