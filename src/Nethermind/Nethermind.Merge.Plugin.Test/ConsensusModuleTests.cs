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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
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
using Nethermind.Trie;

namespace Nethermind.Merge.Plugin.Test
{
    public partial class ConsensusModuleTests
    {
        private static readonly DateTime FirstBerlinBLockDateTime = new DateTime(2021, 4, 15, 10, 7, 3);

        private MergeTestBlockchain Chain { get; set; } = null!;
        private IConsensusRpcModule Rpc { get; set; } = null!;
        private IBlockTree BlockTree => Chain.BlockTree;
        private ITimestamper BerlinTimestamper { get; } = new ManualTimestamper(FirstBerlinBLockDateTime); 
        

        [SetUp]
        public async Task Setup()
        {
            Chain = await CreateBlockChain();
            Rpc = CreateConsensusModule(Chain);
        }
        
        [Test]
        public async Task assembleBlock_should_create_block_on_top_of_genesis()
        {
            Keccak startingHead = BlockTree.HeadHash;
            UInt256 timestamp = BerlinTimestamper.UnixTime.Seconds;
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = startingHead, Timestamp = timestamp};
            ResultWrapper<BlockRequestResult?> response = await Rpc.consensus_assembleBlock(assembleBlockRequest);

            BlockRequestResult expected = CreateParentBlockRequestOnHead();
            expected.GasLimit = 4000000L;
            expected.BlockHash = new Keccak("0x43ec3679c59522fca8351cab097870a5793f63518fb55f49bab8c6e6cca7f9fa");
            expected.LogsBloom = Bloom.Empty;
            expected.Miner = Chain.MinerAddress;
            expected.Number = 1;
            expected.ParentHash = startingHead;
            expected.SetTransactions(Array.Empty<Transaction>());
            expected.Timestamp = timestamp;
            
            response.Data.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public async Task assembleBlock_should_not_create_block_with_unknown_parent()
        {
            Keccak notExistingHash = TestItem.KeccakH;
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = notExistingHash};
            ResultWrapper<BlockRequestResult?> response = await Rpc.consensus_assembleBlock(assembleBlockRequest);
            response.Data.Should().BeNull();
        }
        
        [Test]
        public async Task newBlock_accepts_previously_assembled_block()
        {
            Keccak startingHead = BlockTree.HeadHash;
            BlockHeader startingBestSuggestedHeader = BlockTree.BestSuggestedHeader!;
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = startingHead};
            ResultWrapper<BlockRequestResult?> assembleBlockResult = await Rpc.consensus_assembleBlock(assembleBlockRequest);
            assembleBlockResult.Data!.ParentHash.Should().Be(startingHead);
            
            ResultWrapper<NewBlockResult> newBlockResult = await Rpc.consensus_newBlock(assembleBlockResult.Data!);
            newBlockResult.Data.Valid.Should().BeTrue();
            
            Keccak bestSuggestedHeaderHash = BlockTree.BestSuggestedHeader!.Hash!;
            bestSuggestedHeaderHash.Should().Be(assembleBlockResult.Data!.BlockHash);
            bestSuggestedHeaderHash.Should().NotBe(startingBestSuggestedHeader!.Hash!);
        } 
        
        [Test]
        public async Task setHead_should_change_head()
        {
            Keccak startingHead = BlockTree.HeadHash;

            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(), 
                TestItem.AddressD);
            ResultWrapper<NewBlockResult> newBlockResult = await Rpc.consensus_newBlock(blockRequestResult);
            newBlockResult.Data.Valid.Should().BeTrue();
            
            Keccak newHeadHash = blockRequestResult.BlockHash;
            ResultWrapper<Result> setHeadResult = await Rpc.consensus_setHead(newHeadHash!);
            setHeadResult.Data.Should().Be(Result.Success);
            
            Keccak actualHead = BlockTree.HeadHash;
            actualHead.Should().NotBe(startingHead);
            actualHead.Should().Be(newHeadHash); 
        }

        [Test]
        public async Task finaliseBlock_should_succeed()
        {
            ResultWrapper<Result> resultWrapper = await Rpc.consensus_finaliseBlock(TestItem.KeccakE);
            resultWrapper.Data.Should().Be(Result.Success);
        }
        
        [Test]
        public async Task newBlock_accepts_first_block()
        {
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(), 
                TestItem.AddressD);
            ResultWrapper<NewBlockResult> resultWrapper = await Rpc.consensus_newBlock(blockRequestResult);
            resultWrapper.Data.Valid.Should().BeTrue();
            new BlockRequestResult(BlockTree.BestSuggestedBody).Should().BeEquivalentTo(blockRequestResult);
        }

        [TestCase(30)]
        public async Task can_progress_chain_one_by_one(int count)
        {
            Keccak lastHash = (await ProduceBranch(count, BlockTree.HeadHash, true)).Last().BlockHash;
            BlockTree.HeadHash.Should().Be(lastHash);
            Block? last = RunForAllBlocksInBranch(BlockTree.HeadHash, b => b.IsGenesis, true);
            last.Should().NotBeNull();
            last!.IsGenesis.Should().BeTrue();
        }

        [Test]
        public async Task setHead_can_reorganize_to_any_block()
        {
            async Task CanReorganizeToBlock(BlockRequestResult block)
            {
                ResultWrapper<Result> result = await Rpc.consensus_setHead(block.BlockHash);
                result.Data.Should().Be(Result.Success);
                BlockTree.HeadHash.Should().Be(block.BlockHash);
                BlockTree.Head!.Number.Should().Be(block.Number);
                Chain.State.StateRoot.Should().Be(BlockTree.Head!.StateRoot!);
            }
            
            async Task CanReorganizeToAnyBlock(params IReadOnlyList<BlockRequestResult>[] branches)
            {
                foreach (var branch in branches)
                {
                    await CanReorganizeToBlock(branch.Last());
                }
                
                foreach (var branch in branches)
                {
                    foreach (BlockRequestResult block in branch)
                    {
                        await CanReorganizeToBlock(block);
                    }
                    
                    foreach (BlockRequestResult block in branch.Reverse())
                    {
                        await CanReorganizeToBlock(block);
                    }
                }
            }
            
            IReadOnlyList<BlockRequestResult> branch1 = await ProduceBranch(10, BlockTree.HeadHash, false);
            IReadOnlyList<BlockRequestResult> branch2 = await ProduceBranch(5, branch1[3].BlockHash, false);
            branch2.Last().Number.Should().Be(1 + 3 + 5);
            IReadOnlyList<BlockRequestResult> branch3 = await ProduceBranch(7, branch1[7].BlockHash, false);
            branch3.Last().Number.Should().Be(1 + 7 + 7);
            IReadOnlyList<BlockRequestResult> branch4 = await ProduceBranch(3, branch3[4].BlockHash, false);
            branch3.Last().Number.Should().Be(1 + 7 + 4 + 3);

            await CanReorganizeToAnyBlock(branch1, branch2, branch3, branch4);
        }

        [Test]
        public async Task assembleBlock_can_build_on_any_block()
        {
            async Task CanAssembleOnBlock(BlockRequestResult block)
            {
                UInt256 timestamp = BerlinTimestamper.UnixTime.Seconds;
                AssembleBlockRequest assembleBlockRequest = new() {ParentHash = block.BlockHash, Timestamp = timestamp};
                ResultWrapper<BlockRequestResult?> response = await Rpc.consensus_assembleBlock(assembleBlockRequest);

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

            IReadOnlyList<BlockRequestResult> branch1 = await ProduceBranch(10, BlockTree.HeadHash, false);
            IReadOnlyList<BlockRequestResult> branch2 = await ProduceBranch(5, branch1[3].BlockHash, false);
            branch2.Last().Number.Should().Be(1 + 3 + 5);
            IReadOnlyList<BlockRequestResult> branch3 = await ProduceBranch(7, branch1[7].BlockHash, false);
            branch3.Last().Number.Should().Be(1 + 7 + 7);
            IReadOnlyList<BlockRequestResult> branch4 = await ProduceBranch(3, branch3[4].BlockHash, false);
            branch3.Last().Number.Should().Be(1 + 7 + 4 + 3);
            
            await CanAssembleOnAnyBlock(branch1, branch2, branch3, branch4);
        }
        
        [Test]
        public async Task newBlock_processes_passed_transactions([Values(false, true)] bool moveHead)
        {
            
            IReadOnlyList<BlockRequestResult> branch = await ProduceBranch(10, BlockTree.HeadHash, moveHead);

            foreach (BlockRequestResult block in branch)
            {
                uint count = 10;
                BlockRequestResult newBlockRequest = CreateBlockRequest(block, TestItem.AddressA);
                PrivateKey from = TestItem.PrivateKeyB;
                Address to = TestItem.AddressD;
                var (fromBalanceAfter, toBalanceAfter) = AddTransactions(newBlockRequest, from, to, count, 1, out var parentHeader);

                newBlockRequest.GasUsed = GasCostOf.Transaction * count;
                newBlockRequest.StateRoot = new Keccak("0x3d2e3ced6da0d1e94e65894dc091190480f045647610ef614e1cab4241ca66e0");
                newBlockRequest.ReceiptsRoot = new Keccak("0xc538d36ed1acf6c28187110a2de3e5df707d6d38982f436eb0db7a623f9dc2cd");
                newBlockRequest.BlockHash = newBlockRequest.CalculateHash();
                ResultWrapper<NewBlockResult> result = await Rpc.consensus_newBlock(newBlockRequest);

                result.Data.Valid.Should().BeTrue();
                RootCheckVisitor rootCheckVisitor = new();
                Chain.StateReader.RunTreeVisitor(rootCheckVisitor, newBlockRequest.StateRoot);
                rootCheckVisitor.HasRoot.Should().BeTrue();
                // Chain.StateReader.GetBalance(newBlockRequest.StateRoot, from.Address).Should().Be(fromBalanceAfter);
                Chain.StateReader.GetBalance(newBlockRequest.StateRoot, to).Should().Be(toBalanceAfter);
                if (moveHead)
                {
                    await Rpc.consensus_setHead(newBlockRequest.BlockHash);
                    Chain.State.StateRoot.Should().Be(newBlockRequest.StateRoot);
                    Chain.State.StateRoot.Should().NotBe(parentHeader.StateRoot!);
                }
            }
        }

        [Test]
        public async Task assembleBlock_picks_transactions_from_pool()
        {
            Keccak startingHead = BlockTree.HeadHash;
            uint count = 3;
            int value = 10;
            Address recipient = TestItem.AddressD;
            PrivateKey sender = TestItem.PrivateKeyB;
            Transaction[] transactions = BuildTransactions(startingHead, sender, recipient, count, value, out _, out _);
            Chain.AddTransactions(transactions);
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = startingHead};
            BlockRequestResult assembleBlockResult = (await Rpc.consensus_assembleBlock(assembleBlockRequest)).Data!;

            assembleBlockResult.StateRoot.Should().NotBe(BlockTree.Genesis!.StateRoot!);
            
            Transaction[] transactionsInBlock = assembleBlockResult.GetTransactions();
            transactionsInBlock.Should().BeEquivalentTo(transactions, 
                o => o.Excluding(t => t.ChainId)
                    .Excluding(t => t.SenderAddress)
                    .Excluding(t => t.Timestamp)
                    .Excluding(t => t.PoolIndex));

            ResultWrapper<NewBlockResult> newBlockResult = await Rpc.consensus_newBlock(assembleBlockResult);
            newBlockResult.Data.Valid.Should().BeTrue();

            UInt256 totalValue = ((int)(count * value)).GWei();
            Chain.StateReader.GetBalance(assembleBlockResult.StateRoot, recipient).Should().Be(totalValue);
        }
        
        private (UInt256, UInt256) AddTransactions(BlockRequestResult newBlockRequest, PrivateKey from, Address to, uint count, int value, out BlockHeader parentHeader)
        {
            Transaction[] transactions = BuildTransactions(newBlockRequest.ParentHash, from, to, count, value, out Account accountFrom, out parentHeader);
            newBlockRequest.SetTransactions(transactions);
            UInt256 totalValue = ((int)(count * value)).GWei();
            return (accountFrom.Balance - totalValue, Chain.StateReader.GetBalance(parentHeader.StateRoot!, to) + totalValue);
        }

        private Transaction[] BuildTransactions(Keccak parentHash, PrivateKey from, Address to, uint count, int value, out Account accountFrom, out BlockHeader parentHeader)
        {
            Transaction BuildTransaction(uint index, Account senderAccount) =>
                Build.A.Transaction.WithNonce(senderAccount.Nonce + index)
                    .WithTimestamp(BerlinTimestamper.UnixTime.Seconds)
                    .WithTo(to)
                    .WithValue(value.GWei())
                    .WithGasPrice(1.GWei())
                    .WithChainId(Chain.SpecProvider.ChainId)
                    .WithSenderAddress(from.Address)
                    .SignedAndResolved(from)
                    .TestObject;

            parentHeader = BlockTree.FindHeader(parentHash, BlockTreeLookupOptions.None)!;
            Account account = Chain.StateReader.GetAccount(parentHeader.StateRoot!, @from.Address)!;
            accountFrom = account;

            return Enumerable.Range(0, (int)count)
                .Select(i => BuildTransaction((uint)i, account)).ToArray();
        }

        private BlockRequestResult CreateParentBlockRequestOnHead()
        {
            Block? head = BlockTree.Head;
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
            blockRequest.BlockHash = blockRequest.CalculateHash();
            return blockRequest;
        }
        
        private async Task<IReadOnlyList<BlockRequestResult>> ProduceBranch(int count, Keccak parentBlockHash, bool setHead)
        {
            List<BlockRequestResult> blocks = new();
            ManualTimestamper timestamper = new(FirstBerlinBLockDateTime);
            for (int i = 0; i < count; i++)
            {
                AssembleBlockRequest assembleBlockRequest = new() {ParentHash = parentBlockHash, Timestamp = ((ITimestamper) timestamper).UnixTime.Seconds};
                BlockRequestResult assembleBlockResponse = (await Rpc.consensus_assembleBlock(assembleBlockRequest)).Data!;
                NewBlockResult newBlockResponse = (await Rpc.consensus_newBlock(assembleBlockResponse!)).Data;
                newBlockResponse.Valid.Should().BeTrue();
                if (setHead)
                {
                    Keccak newHead = assembleBlockResponse.BlockHash;
                    ResultWrapper<Result> setHeadResponse = await Rpc.consensus_setHead(newHead);
                    setHeadResponse.Data.Should().Be(Result.Success);
                    BlockTree.HeadHash.Should().Be(newHead);
                }
                blocks.Add((assembleBlockResponse));
                parentBlockHash = assembleBlockResponse.BlockHash;
                timestamper.Add(TimeSpan.FromSeconds(12));
            }

            return blocks;
        }
        
        private Block? RunForAllBlocksInBranch(Keccak blockHash, Func<Block, bool> shouldStop, bool requireCanonical)
        {
            var options = requireCanonical ? BlockTreeLookupOptions.RequireCanonical : BlockTreeLookupOptions.None;
            Block? current = BlockTree.FindBlock(blockHash, options);
            while (current is not null && !shouldStop(current))
            {
                current = BlockTree.FindParent(current, options);
            }

            return current;
        }
    }
}
