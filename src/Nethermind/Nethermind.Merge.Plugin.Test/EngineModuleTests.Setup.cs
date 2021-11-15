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
using Nethermind.Consensus.Transactions;
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
using Nethermind.Merge.Plugin.Handlers.V1;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Test
{
    public partial class EngineModuleTests
    {
        private async Task<MergeTestBlockchain> CreateBlockChain() => await new MergeTestBlockchain(new ManualTimestamper()).Build(new SingleReleaseSpecProvider(London.Instance, 1));

        private IEngineRpcModule CreateEngineModule(MergeTestBlockchain chain)
        {
            PayloadStorage? payloadStorage = new(chain.IdealBlockProductionContext, chain.EmptyBlockProductionContext, new InitConfig(), chain.LogManager);
            PayloadService payloadService = new(chain.IdealBlockProductionContext, chain.EmptyBlockProductionContext, new InitConfig(), chain.SealEngine, chain.LogManager);

            return new EngineRpcModule(
                new PreparePayloadHandler(chain.BlockTree, payloadStorage, chain.Timestamper, chain.SealEngine, chain.LogManager),
                new GetPayloadHandler(payloadStorage, chain.LogManager),
                new GetPayloadV1Handler(payloadService, chain.LogManager),
                new ExecutePayloadHandler(chain.HeaderValidator, chain.BlockTree, chain.BlockchainProcessor, chain.EthSyncingInfo, new InitConfig(), chain.LogManager),
                new ExecutePayloadV1Handler(chain.BlockValidator, chain.BlockTree, chain.BlockchainProcessor, chain.EthSyncingInfo, new InitConfig(), chain.LogManager),
                (PoSSwitcher)chain.PoSSwitcher,
                new ForkChoiceUpdatedHandler(chain.BlockTree, chain.State, chain.BlockFinalizationManager, chain.PoSSwitcher, chain.BlockConfirmationManager, chain.LogManager),
                new ForkchoiceUpdatedV1Handler(chain.BlockTree, chain.State, chain.BlockFinalizationManager, chain.PoSSwitcher, chain.EthSyncingInfo, chain.BlockConfirmationManager, payloadService, chain.LogManager),
                new ExecutionStatusHandler(chain.BlockTree, chain.BlockConfirmationManager, chain.BlockFinalizationManager),
                chain.LogManager);
        }

        private class MergeTestBlockchain : TestBlockchain
        {
            public IBlockProducer EmptyBlockProducer { get; private set; }

            public IMergeConfig MergeConfig { get; set; } = new MergeConfig() {Enabled = true};

            public Eth2BlockProductionContext IdealBlockProductionContext { get; set; } = new();

            public Eth2BlockProductionContext EmptyBlockProductionContext { get; set; } = new();
            public MergeTestBlockchain(ManualTimestamper timestamper)
            {
                Timestamper = timestamper;
                GenesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.Genesis
                    .WithTimestamp(UInt256.One);
                Signer = new Eth2Signer(MinerAddress);
                BlockConfirmationManager = new BlockConfirmationManager();
            }
            
            protected override Task AddBlocksOnStart() => Task.CompletedTask;

            public sealed override ILogManager LogManager { get; } = new NUnitLogManager();
            
            public IEthSyncingInfo EthSyncingInfo { get; private set; }

            private ISigner Signer { get; }

            protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
            {
                MiningConfig miningConfig = new() { Enabled = true, MinGasPrice = 0 };
                TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(SpecProvider, miningConfig);
                EthSyncingInfo = new EthSyncingInfo(BlockTree);
                Eth2BlockProducerFactory? blockProducerFactory = new Eth2BlockProducerFactory(
                    SpecProvider,
                    SealEngine,
                    Timestamper,
                    miningConfig,
                    LogManager,
                    targetAdjustedGasLimitCalculator);

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
                
                EmptyBlockProductionContext.Init(blockProducerEnvFactory);
                EmptyBlockProducer = blockProducerFactory.Create(
                    EmptyBlockProductionContext,
                    EmptyTxSource.Instance
                );
                
                EmptyBlockProducer.Start();
                IdealBlockProductionContext.Init(blockProducerEnvFactory);
                return blockProducerFactory.Create(
                    IdealBlockProductionContext);
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
                PoSSwitcher = new PoSSwitcher(LogManager, MergeConfig, new MemDb(), BlockTree, SpecProvider);
                SealEngine = new MergeSealEngine(SealEngine, PoSSwitcher, Signer, LogManager);
                HeaderValidator = new PostMergeHeaderValidator(PoSSwitcher, BlockTree, SpecProvider, SealEngine, LogManager);
                
                return new BlockValidator(
                    new TxValidator(SpecProvider.ChainId),
                    HeaderValidator,
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
