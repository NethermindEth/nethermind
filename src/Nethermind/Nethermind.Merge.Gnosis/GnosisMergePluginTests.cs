using System;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Timers;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Gnosis.Tests
{
    class MergeAuRaTestBlockchain : EngineModuleTests.MergeTestBlockchain
    {
        public MergeAuRaTestBlockchain(IMergeConfig? mergeConfig = null, IPayloadPreparationService? mockedPayloadPreparationService = null)
         : base(mergeConfig, mockedPayloadPreparationService)
        {
            SealEngineType = Nethermind.Core.SealEngineType.AuRa;
        }

        protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
        {
            Address feeRecipient = !string.IsNullOrEmpty(MergeConfig.FeeRecipient) ? new Address(MergeConfig.FeeRecipient) : Address.Zero;
            SealEngine = new MergeSealEngine(SealEngine, PoSSwitcher, feeRecipient, SealValidator, LogManager);
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
                blockProducerEnv, ((EngineModuleTests.MergeTestBlockchain)this).BlockProductionTrigger);
            PostMergeBlockProducer = postMergeBlockProducer;
            PayloadPreparationService ??= new PayloadPreparationService(postMergeBlockProducer, BlockProductionTrigger, SealEngine,
                MergeConfig, TimerFactory.Default, LogManager);

            IAuRaStepCalculator auraStepCalculator = Substitute.For<IAuRaStepCalculator>();
            auraStepCalculator.TimeToNextStep.Returns(TimeSpan.FromMilliseconds(0));
            FollowOtherMiners gasLimitCalculator = new(MainnetSpecProvider.Instance);
            AuRaBlockProducer preMergeBlockProducer = new(
                txPoolTxSource,
                blockProducerEnvFactory.Create().ChainProcessor,
                ((TestBlockchain)this).BlockProductionTrigger,
                State,
                sealer,
                BlockTree,
                Timestamper,
                auraStepCalculator,
                NullReportingValidator.Instance,
                new AuRaConfig(),
                gasLimitCalculator,
                SpecProvider,
                LogManager
            );

            return new MergeBlockProducer(preMergeBlockProducer, postMergeBlockProducer, PoSSwitcher);
        }

    }

    class GnosisMergeEngineModuleTests : EngineModuleTests
    {
        protected override async Task<MergeTestBlockchain> CreateBlockChain(IMergeConfig? mergeConfig = null, IPayloadPreparationService? mockedPayloadService = null)
        {
            return await new MergeAuRaTestBlockchain(mergeConfig, mockedPayloadService)
                .Build(new SingleReleaseSpecProvider(London.Instance, 1));
        }

        [Test]
        public void Trigger()
        {
        }
    }
}
