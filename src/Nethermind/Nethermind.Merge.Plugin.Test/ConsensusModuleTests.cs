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
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Result = Nethermind.Merge.Plugin.Data.Result;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Test
{
    public partial class ConsensusModuleTests
    {
        private MergeTestBlockchain _chain = null!;
        private IConsensusRpcModule _consensusRpcModule = null!;
        private readonly DateTime _firstBerlinBLockDateTime = new DateTime(2021, 4, 15, 10, 7, 3);
        private IBlockTree BlockTree => _chain.BlockTree;

        [SetUp]
        public async Task Setup()
        {
            _chain = await CreateBlockChain();
            _consensusRpcModule = CreateConsensusModule(_chain);
        }
        
        [Test]
        public async Task assembleBlock_should_create_block_on_top_of_genesis()
        {
            Keccak startingHead = BlockTree.HeadHash;
            ITimestamper timestamper = new ManualTimestamper(_firstBerlinBLockDateTime); 
            UInt256 timestamp = timestamper.UnixTime.Seconds;
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = startingHead, Timestamp = timestamp};
            ResultWrapper<BlockRequestResult?> response = await _consensusRpcModule.consensus_assembleBlock(assembleBlockRequest);

            BlockRequestResult expected = CreateParentBlockRequestOnHead();
            expected.GasLimit = 4000000L;
            expected.BlockHash = new Keccak("0x43ec3679c59522fca8351cab097870a5793f63518fb55f49bab8c6e6cca7f9fa");
            expected.LogsBloom = Bloom.Empty;
            expected.Miner = _chain.MinerAddress;
            expected.Number = 1;
            expected.ParentHash = startingHead;
            expected.Transactions = Rlp.Encode(Array.Empty<Transaction>()).Bytes;
            expected.Timestamp = timestamp;
            
            response.Data.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public async Task assembleBlock_should_not_create_block_with_unknown_parent()
        {
            Keccak notExistingHash = TestItem.KeccakH;
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = notExistingHash};
            ResultWrapper<BlockRequestResult?> response = await _consensusRpcModule.consensus_assembleBlock(assembleBlockRequest);
            response.Data.Should().BeNull();
        }
        
        [Test]
        public async Task newBlock_accepts_previously_assembled_block()
        {
            Keccak startingHead = BlockTree.HeadHash;
            BlockHeader startingBestSuggestedHeader = BlockTree.BestSuggestedHeader!;
            AssembleBlockRequest assembleBlockRequest = new() {ParentHash = startingHead};
            ResultWrapper<BlockRequestResult?> assembleBlockResult = await _consensusRpcModule.consensus_assembleBlock(assembleBlockRequest);
            assembleBlockResult.Data!.ParentHash.Should().Be(startingHead);
            
            ResultWrapper<NewBlockResult> newBlockResult = await _consensusRpcModule.consensus_newBlock(assembleBlockResult.Data!);
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
            ResultWrapper<NewBlockResult> newBlockResult = await _consensusRpcModule.consensus_newBlock(blockRequestResult);
            newBlockResult.Data.Valid.Should().BeTrue();
            
            Keccak newHeadHash = blockRequestResult.BlockHash;
            ResultWrapper<Result> setHeadResult = await _consensusRpcModule.consensus_setHead(newHeadHash!);
            setHeadResult.Data.Should().Be(Result.Success);
            
            Keccak actualHead = BlockTree.HeadHash;
            actualHead.Should().NotBe(startingHead);
            actualHead.Should().Be(newHeadHash); 
        }

        [Test]
        public async Task finaliseBlock_should_succeed()
        {
            ResultWrapper<Result> resultWrapper = await _consensusRpcModule.consensus_finaliseBlock(TestItem.KeccakE);
            resultWrapper.Data.Should().Be(Result.Success);
        }
        
        [Test]
        public async Task newBlock_accepts_first_block()
        {
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                CreateParentBlockRequestOnHead(), 
                TestItem.AddressD);
            ResultWrapper<NewBlockResult> resultWrapper = await _consensusRpcModule.consensus_newBlock(blockRequestResult);
            resultWrapper.Data.Valid.Should().BeTrue();
            new BlockRequestResult(BlockTree.BestSuggestedBody).Should().BeEquivalentTo(blockRequestResult);
        }

        [TestCase(30)]
        public async Task can_progress_chain_one_by_one(int count)
        {
            Keccak lastHash = (await ProduceBranch(count, BlockTree.HeadHash, true)).Last().Hash;
            BlockTree.HeadHash.Should().Be(lastHash);
            Block? last = RunForAllBlocksInBranch(BlockTree.HeadHash, b => b.IsGenesis, true);
            last.Should().NotBeNull();
            last!.IsGenesis.Should().BeTrue();
        }

        [Test]
        public async Task setHead_can_reorganize_to_any_block()
        {
            async Task CanReorganizeToBlock((Keccak Hash, long Number) block)
            {
                ResultWrapper<Result> result = await _consensusRpcModule.consensus_setHead(block.Hash);
                result.Data.Should().Be(Result.Success);
                BlockTree.HeadHash.Should().Be(block.Hash);
                BlockTree.Head!.Number.Should().Be(block.Number);
                _chain.State.StateRoot.Should().Be(BlockTree.Head!.StateRoot!);
            }
            
            async Task CanReorganizeToAnyBlock(params IReadOnlyList<(Keccak Hash, long Number)>[] branches)
            {
                foreach (var branch in branches)
                {
                    await CanReorganizeToBlock(branch.Last());
                }
                
                foreach (var branch in branches)
                {
                    foreach ((Keccak Hash, long Number) block in branch)
                    {
                        await CanReorganizeToBlock(block);
                    }
                    
                    foreach ((Keccak Hash, long Number) block in branch.Reverse())
                    {
                        await CanReorganizeToBlock(block);
                    }
                }
            }
            
            IReadOnlyList<(Keccak Hash, long Number)> branch1 = await ProduceBranch(10, BlockTree.HeadHash, false);
            IReadOnlyList<(Keccak Hash, long Number)> branch2 = await ProduceBranch(5, branch1[3].Hash, false);
            branch2.Last().Number.Should().Be(1 + 3 + 5);
            IReadOnlyList<(Keccak Hash, long Number)> branch3 = await ProduceBranch(7, branch1[7].Hash, false);
            branch3.Last().Number.Should().Be(1 + 7 + 7);
            IReadOnlyList<(Keccak Hash, long Number)> branch4 = await ProduceBranch(3, branch3[4].Hash, false);
            branch3.Last().Number.Should().Be(1 + 7 + 4 + 3);

            await CanReorganizeToAnyBlock(branch1, branch2, branch3, branch4);
        }

        [Test]
        public async Task newBlock_processes_passed_transactions()
        {
            
        }
        
        [Test]
        public async Task assembleBlock_picks_transactions_from_pool()
        {
            
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
                LogsBloom = Bloom.Empty,
                Transactions = Rlp.Encode(Array.Empty<Transaction>()).Bytes
            };
            
            blockRequest.BlockHash = blockRequest.ToBlock().CalculateHash();
            return blockRequest;
        }
        
        private async Task<IReadOnlyList<(Keccak Hash, long Number)>> ProduceBranch(int count, Keccak parentBlockHash, bool setHead)
        {
            List<(Keccak, long)> blocks = new();
            ManualTimestamper timestamper = new(_firstBerlinBLockDateTime);
            for (int i = 0; i < count; i++)
            {
                AssembleBlockRequest assembleBlockRequest = new() {ParentHash = parentBlockHash, Timestamp = ((ITimestamper) timestamper).UnixTime.Seconds};
                BlockRequestResult assembleBlockResponse = (await _consensusRpcModule.consensus_assembleBlock(assembleBlockRequest)).Data!;
                NewBlockResult newBlockResponse = (await _consensusRpcModule.consensus_newBlock(assembleBlockResponse!)).Data;
                newBlockResponse.Valid.Should().BeTrue();
                if (setHead)
                {
                    Keccak newHead = assembleBlockResponse.BlockHash;
                    ResultWrapper<Result> setHeadResponse = await _consensusRpcModule.consensus_setHead(newHead);
                    setHeadResponse.Data.Should().Be(Result.Success);
                    BlockTree.HeadHash.Should().Be(newHead);
                }
                blocks.Add((assembleBlockResponse.BlockHash, assembleBlockResponse.Number));
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
