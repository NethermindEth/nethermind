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
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using NUnit.Framework;
using Result = Nethermind.Merge.Plugin.Data.Result;

namespace Nethermind.Merge.Plugin.Test
{
    public class ConsensusModuleTests
    {
        private MergeTestBlockchain _chain;

        private IConsensusRpcModule _consensusRpcModule;

        [SetUp]
        public void Setup()
        {
            _chain = CreateBlockChain();
            _consensusRpcModule = CreateConsensusModule(_chain);
        }
        
        [Test]
        public void consensus_assembleBlock_should_return_expected_results()
        {
 
        }
        
        [Test]
        public void consensus_finaliseBlock_should_succeed()
        {
            ResultWrapper<Result> resultWrapper = _consensusRpcModule.consensus_finaliseBlock(TestItem.KeccakE);
            resultWrapper.Data.Should().Be(Result.Success);
        }
        
        [Test]
        public void consensus_newBlock_accepts_first_block()
        {
            BlockRequestResult blockRequestResult = CreateBlockRequest(
                new BlockRequestResult(true) {Number = 0, ParentHash = _chain.BlockTree.GenesisHash}, 
                TestItem.AddressD);
            ResultWrapper<NewBlockResult> resultWrapper = _consensusRpcModule.consensus_newBlock(blockRequestResult);
            resultWrapper.Data.Should().Be(Result.Success);
        }

        private static BlockRequestResult CreateBlockRequest(BlockRequestResult parent, Address miner)
        {
            BlockRequestResult blockRequest = new BlockRequestResult(true)
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
            
            blockRequest.BlockHash = new Keccak(Rlp.Encode(blockRequest.ToBlock().Header, RlpBehaviors.ForSealing).Bytes);
            return blockRequest;
        }

        private MergeTestBlockchain CreateBlockChain() => new MergeTestBlockchain();

        private IConsensusRpcModule CreateConsensusModule(MergeTestBlockchain chain)
        {
            return new ConsensusRpcModule(
                new AssembleBlockHandler(chain.BlockTree, (IEth2BlockProducer) chain.BlockProducer, chain.LogManager),
                new NewBlockHandler(chain.BlockTree, chain.BlockchainProcessor, chain.State, chain.LogManager),
                new SetHeadBlockHandler(chain.BlockTree, chain.LogManager),
                new FinaliseBlockHandler());
        }

        private class MergeTestBlockchain : TestBlockchain
        {
            protected override Task AddBlocksOnStart() => Task.CompletedTask;

            public override ILogManager LogManager { get; } = new NUnitLogManager();

            protected override ITestBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, BlockchainProcessor chainProcessor, IStateProvider producerStateProvider, ISealer sealer)
            {
                Signer signer = new Signer(SpecProvider.ChainId, TestItem.PrivateKeyA, LogManager);
                return (ITestBlockProducer) new Eth2TestBlockProducerFactory().Create(
                    BlockTree,
                    DbProvider,
                    ReadOnlyTrieStore,
                    new RecoverSignatures(new EthereumEcdsa(SpecProvider.ChainId, LogManager), TxPool, SpecProvider, LogManager),
                    TxPool,
                    new BlockValidator(
                        new TxValidator(SpecProvider.ChainId),
                        new HeaderValidator(BlockTree, new Eth2SealEngine(signer), SpecProvider, LogManager),
                        Always.Valid,
                        SpecProvider,
                        LogManager),
                    new RewardCalculator(SpecProvider),
                    ReceiptStorage,
                    BlockProcessingQueue,
                    State,
                    SpecProvider,
                    signer,
                    new MiningConfig(),
                    LogManager);
            }
        }
    }
}
