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
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Test
{
    public partial class EngineModuleTests
    {
        private async Task<MergeTestBlockchain> CreateBlockChain() => await new MergeTestBlockchain(new ManualTimestamper()).Build(new SingleReleaseSpecProvider(Berlin.Instance, 1));

        private IEngineRpcModule CreateEngineModule(MergeTestBlockchain chain)
        {
            PayloadStorage? payloadStorage = new(chain.BlockProductionTrigger, chain.EmptyBlockProducerTrigger, chain.State, chain.BlockchainProcessor, new InitConfig());
            PayloadManager payloadManager = new(chain.BlockTree);
            return new EngineRpcModule(
                new PreparePayloadHandler(chain.BlockTree, payloadStorage, chain.BlockProductionTrigger, chain.EmptyBlockProducerTrigger, chain.Timestamper, chain.SealEngine, chain.LogManager),
                new GetPayloadHandler(payloadStorage,  chain.LogManager),
                new ExecutePayloadHandler(chain.BlockTree, chain.BlockPreprocessorStep, chain.BlockchainProcessor, payloadManager, new EthSyncingInfo(chain.BlockFinder), chain.State, new InitConfig(), chain.LogManager),
                new ConsensusValidatedHandler(payloadManager),
                (PoSSwitcher)chain.PoSSwitcher,
                new ForkChoiceUpdatedHandler(chain.BlockTree, chain.State, chain.BlockFinalizationManager, chain.PoSSwitcher, chain.BlockConfirmationManager, chain.LogManager),
                new ExecutionStatusHandler(chain.BlockTree, chain.BlockConfirmationManager, chain.BlockFinalizationManager),
                chain.LogManager);
        }

        private class MergeTestBlockchain : TestBlockchain
        {
            public IBlockProducer EmptyBlockProducer { get; private set; }
            public BuildBlocksWhenRequested EmptyBlockProducerTrigger { get; private set; } = new ();
            public MergeTestBlockchain(ManualTimestamper timestamper)
            {
                Timestamper = timestamper;
                GenesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.Genesis
                    .WithTimestamp(UInt256.One);
                Signer = new Eth2Signer(MinerAddress);
                PoSSwitcher = new PoSSwitcher(LogManager, new MergeConfig() { Enabled = true }, new MemDb());
                SealEngine = new MergeSealEngine(Substitute.For<ISealEngine>(), PoSSwitcher, Signer);
                BlockConfirmationManager = new BlockConfirmationManager();
            }
            
            protected override Task AddBlocksOnStart() => Task.CompletedTask;

            public sealed override ILogManager LogManager { get; } = new NUnitLogManager();
            
            private IBlockValidator BlockValidator { get; set; } = null!;

            private ISigner Signer { get; }
            
            public IPoSSwitcher PoSSwitcher { get; }
            
            public ISealEngine SealEngine { get; }

            protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
            {
                MiningConfig miningConfig = new();
                TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(SpecProvider, miningConfig);
                
                BlockProducerEnvFactory blockProducerEnvFactory = new(
                    DbProvider, 
                    BlockTree, 
                    ReadOnlyTrieStore, 
                    SpecProvider, 
                    BlockValidator,
                    NoBlockRewards.Instance,
                    ReceiptStorage,
                    BlockPreprocessorStep,
                    TxPool,
                    transactionComparerProvider,
                    miningConfig,
                    LogManager);
                
                EmptyBlockProducer = new Eth2EmptyBlockProducerFactory().Create(
                    blockProducerEnvFactory,
                    BlockTree,
                    EmptyBlockProducerTrigger,
                    SpecProvider,
                    SealEngine,
                    Timestamper,
                    miningConfig,
                    LogManager
                );
                
                EmptyBlockProducer.Start();
                
                return new Eth2TestBlockProducerFactory(targetAdjustedGasLimitCalculator).Create(
                    blockProducerEnvFactory,
                    BlockTree,
                    BlockProductionTrigger,
                    SpecProvider,
                    SealEngine,
                    Timestamper,
                    miningConfig,
                    LogManager);
            }
            
            protected override BlockProcessor CreateBlockProcessor()
            {
                BlockValidator = CreateBlockValidator();
                return new BlockProcessor(
                    SpecProvider,
                    BlockValidator,
                    NoBlockRewards.Instance,
                    new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, State),
                    State,
                    Storage,
                    ReceiptStorage,
                    NullWitnessCollector.Instance,
                    LogManager);
            }

            private IBlockValidator CreateBlockValidator()
            {
                HeaderValidator headerValidator =
                    new (BlockTree, Always.Valid, SpecProvider, LogManager);
                HeaderValidator mergeHeaderValidator =
                new MergeHeaderValidator(headerValidator, BlockTree, SpecProvider, PoSSwitcher, LogManager);
                
                return new BlockValidator(
                    new TxValidator(SpecProvider.ChainId),
                    mergeHeaderValidator,
                    Always.Valid,
                    SpecProvider,
                    LogManager);
            }

            public Address MinerAddress => TestItem.PrivateKeyA.Address;
            public IManualBlockFinalizationManager BlockFinalizationManager { get; } = new ManualBlockFinalizationManager();

            protected override async Task<TestBlockchain> Build(ISpecProvider? specProvider = null, UInt256? initialValues = null)
            {
                TestBlockchain chain = await base.Build(specProvider, initialValues);
                await chain.BlockchainProcessor.StopAsync(true);
                Suggester.Dispose();
                return chain;
            }

            public async Task<MergeTestBlockchain> Build(ISpecProvider? specProvider = null) => 
                (MergeTestBlockchain) await Build(specProvider, null);
        }
    }
}
