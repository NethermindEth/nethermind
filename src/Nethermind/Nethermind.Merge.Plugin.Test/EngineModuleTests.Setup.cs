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
using Nethermind.Blockchain.Synchronization;
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
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Handlers.V1;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Test
{
    public partial class EngineModuleTests
    {
        private async Task<MergeTestBlockchain> CreateBlockChain(IMergeConfig mergeConfig = null, IPayloadPreparationService? mockedPayloadService = null) 
            => await new MergeTestBlockchain(mergeConfig, mockedPayloadService)
                .Build(
                    new SingleReleaseSpecProvider(London.Instance, 1));

        private IEngineRpcModule CreateEngineModule(MergeTestBlockchain chain, ISyncConfig? syncConfig = null)
        {
            IPeerRefresher peerRefresher = Substitute.For<IPeerRefresher>();
            
            chain.BeaconPivot = new BeaconPivot(syncConfig ?? new SyncConfig(), chain.MergeConfig, new MemDb(), chain.BlockTree, peerRefresher, chain.LogManager);
            BlockCacheService blockCacheService = new();
            chain.BeaconSync = new BeaconSync(chain.BeaconPivot, chain.BlockTree, syncConfig ?? new SyncConfig(), blockCacheService, chain.LogManager);
            return new EngineRpcModule(
                new GetPayloadV1Handler(chain.PayloadPreparationService!, chain.LogManager),
                new NewPayloadV1Handler(chain.BlockValidator, chain.BlockTree, chain.BlockchainProcessor, new InitConfig(), chain.PoSSwitcher, chain.BeaconSync, chain.BeaconPivot, blockCacheService, chain.BlockProcessingQueue, chain.BeaconSync, chain.LogManager),
                new ForkchoiceUpdatedV1Handler(chain.BlockTree, chain.BlockFinalizationManager, chain.PoSSwitcher, chain.BlockConfirmationManager, chain.PayloadPreparationService!, blockCacheService, chain.BeaconSync, peerRefresher, chain.LogManager),
                new ExecutionStatusHandler(chain.BlockTree, chain.BlockConfirmationManager, chain.BlockFinalizationManager),
                new GetPayloadBodiesV1Handler(chain.BlockTree, chain.LogManager),
                new ExchangeTransitionConfigurationV1Handler(chain.PoSSwitcher, chain.LogManager),
                chain.LogManager);
        }

        private class MergeTestBlockchain : TestBlockchain
        {
            public IMergeConfig MergeConfig { get; set; }
            
            public PostMergeBlockProducer? PostMergeBlockProducer { get; set; }

            public IPayloadPreparationService? PayloadPreparationService { get; set; }

            public ISealValidator SealValidator { get; set; }

            public IManualBlockProductionTrigger BlockProductionTrigger { get; set; } = new BuildBlocksWhenRequested();
            
            public IBeaconPivot BeaconPivot { get; set; }
            
            public BeaconSync BeaconSync { get; set; }

            public MergeTestBlockchain(IMergeConfig? mergeConfig = null, IPayloadPreparationService? mockedPayloadPreparationService = null)
            {
                GenesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.Genesis
                    .WithTimestamp(UInt256.One);
                BlockConfirmationManager = new BlockConfirmationManager();
                MergeConfig = mergeConfig ?? new MergeConfig() { Enabled = true, TerminalTotalDifficulty = "0" };
                PayloadPreparationService = mockedPayloadPreparationService;
            }
            
            protected override Task AddBlocksOnStart() => Task.CompletedTask;

            public sealed override ILogManager LogManager { get; } = new NUnitLogManager();
            
            public IEthSyncingInfo EthSyncingInfo { get; private set; }

            protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
            {
                Address feeRecipient = !string.IsNullOrEmpty(MergeConfig.FeeRecipient) ? new Address(MergeConfig.FeeRecipient) : Address.Zero;
                SealEngine = new MergeSealEngine(SealEngine, PoSSwitcher, feeRecipient, SealValidator, LogManager);
                IBlockProducer preMergeBlockProducer =
                    base.CreateTestBlockProducer(txPoolTxSource, sealer, transactionComparerProvider);
                MiningConfig miningConfig = new() { Enabled = true, MinGasPrice = 0 };
                TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(SpecProvider, miningConfig);
                EthSyncingInfo = new EthSyncingInfo(BlockTree);
                PostMergeBlockProducerFactory? blockProducerFactory = new(
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


                BlockProducerEnv blockProducerEnv = blockProducerEnvFactory.Create();
                PostMergeBlockProducer? postMergeBlockProducer = blockProducerFactory.Create(
                    blockProducerEnv, BlockProductionTrigger);
                PostMergeBlockProducer = postMergeBlockProducer;
                PayloadPreparationService ??= new PayloadPreparationService(postMergeBlockProducer, BlockProductionTrigger, SealEngine,
                    MergeConfig, TimerFactory.Default, LogManager);
                return new MergeBlockProducer(preMergeBlockProducer, postMergeBlockProducer, PoSSwitcher);
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
                IBlockCacheService blockCacheService = new BlockCacheService();
                PoSSwitcher = new PoSSwitcher(MergeConfig, SyncConfig.Default, new MemDb(), BlockTree, SpecProvider, blockCacheService, LogManager);
                SealValidator = new MergeSealValidator(PoSSwitcher, Always.Valid);
                HeaderValidator = new MergeHeaderValidator(PoSSwitcher, BlockTree, SpecProvider, SealValidator, LogManager);
                
                return new BlockValidator(
                    new TxValidator(SpecProvider.ChainId),
                    HeaderValidator,
                    Always.Valid,
                    SpecProvider,
                    LogManager);
            }
            public IManualBlockFinalizationManager BlockFinalizationManager { get; } = new ManualBlockFinalizationManager();

            protected override async Task<TestBlockchain> Build(ISpecProvider? specProvider = null, UInt256? initialValues = null)
            {
                TestBlockchain chain = await base.Build(specProvider, initialValues);
                return chain;
            }

            public async Task<MergeTestBlockchain> Build(ISpecProvider? specProvider = null) => 
                (MergeTestBlockchain) await Build(specProvider, null);
        }
    }
}
