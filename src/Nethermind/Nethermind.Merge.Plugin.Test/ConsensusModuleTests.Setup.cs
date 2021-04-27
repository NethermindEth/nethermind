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

using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Test
{
    public partial class ConsensusModuleTests
    {
        private async Task<MergeTestBlockchain> CreateBlockChain() => await new MergeTestBlockchain().Build(new SingleReleaseSpecProvider(Berlin.Instance, 1));

        private IConsensusRpcModule CreateConsensusModule(MergeTestBlockchain chain)
        {
            return new ConsensusRpcModule(
                new AssembleBlockHandler(chain.BlockTree, (IEth2BlockProducer) chain.BlockProducer, chain.LogManager),
                new NewBlockHandler(chain.BlockTree, chain.BlockPreprocessorStep, chain.BlockchainProcessor, chain.State, chain.LogManager),
                new SetHeadBlockHandler(chain.BlockTree, chain.State, chain.LogManager),
                new FinaliseBlockHandler(chain.BlockFinder, chain.FinalizationManager, chain.LogManager),
                chain.LogManager);
        }

        private class MergeTestBlockchain : TestBlockchain
        {
            public MergeTestBlockchain()
            {
                GenesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.Genesis
                    .WithTimestamp(UInt256.One);
            }
            
            protected override Task AddBlocksOnStart() => Task.CompletedTask;

            public override ILogManager LogManager { get; } = new NUnitLogManager();
            
            private IBlockValidator BlockValidator { get; set; } = null!;

            private ISigner Signer { get; set; } = null!;

            protected override ITestBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, BlockchainProcessor chainProcessor, IStateProvider producerStateProvider, ISealer sealer)
            {
                return (ITestBlockProducer) new Eth2TestBlockProducerFactory().Create(
                    BlockTree,
                    DbProvider,
                    ReadOnlyTrieStore,
                    new RecoverSignatures(new EthereumEcdsa(SpecProvider.ChainId, LogManager), TxPool, SpecProvider, LogManager),
                    TxPool,
                    new BlockValidator(
                        new TxValidator(SpecProvider.ChainId),
                        new HeaderValidator(BlockTree, new Eth2SealEngine(Signer), SpecProvider, LogManager),
                        Always.Valid,
                        SpecProvider,
                        LogManager),
                    NoBlockRewards.Instance,
                    ReceiptStorage,
                    BlockProcessingQueue,
                    State,
                    SpecProvider,
                    Signer,
                    new MiningConfig(),
                    LogManager);
            }
            
            protected override BlockProcessor CreateBlockProcessor()
            {
                Signer = new Eth2Signer(MinerAddress);
                HeaderValidator headerValidator = new(BlockTree, new Eth2SealEngine(Signer), SpecProvider, LogManager);
                BlockValidator = CreateBlockValidator(headerValidator);
                    
                return new BlockProcessor(
                    SpecProvider,
                    BlockValidator,
                    NoBlockRewards.Instance,
                    TxProcessor,
                    State,
                    Storage,
                    ReceiptStorage,
                    NullWitnessCollector.Instance,
                    LogManager);
            }

            private IBlockValidator CreateBlockValidator(HeaderValidator headerValidator) =>
                new BlockValidator(
                    new TxValidator(SpecProvider.ChainId),
                    headerValidator,
                    new OmmersValidator(BlockTree, headerValidator, LogManager),
                    SpecProvider,
                    LogManager);

            public Address MinerAddress => TestItem.PrivateKeyA.Address;
            public IEth2FinalizationManager FinalizationManager { get; } = new Eth2FinalizationManager();

            protected override async Task<TestBlockchain> Build(ISpecProvider? specProvider = null, UInt256? initialValues = null)
            {
                TestBlockchain chain = await base.Build(specProvider, initialValues);
                await chain.BlockchainProcessor.StopAsync(true);
                return chain;
            }

            public async Task<MergeTestBlockchain> Build(ISpecProvider? specProvider = null) => 
                (MergeTestBlockchain) await Build(specProvider, null);
        }
    }
}
