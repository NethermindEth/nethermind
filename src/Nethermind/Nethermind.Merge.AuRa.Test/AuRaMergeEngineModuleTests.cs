// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Requests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Timers;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuRaMergeEngineModuleTests : EngineModuleTests
{
    protected override MergeTestBlockchain CreateBaseBlockchain(
        IMergeConfig? mergeConfig = null,
        IPayloadPreparationService? mockedPayloadService = null,
        ILogManager? logManager = null,
        IConsensusRequestsProcessor? mockedConsensusRequestsProcessor = null)
        => new MergeAuRaTestBlockchain(mergeConfig, mockedPayloadService, null, logManager, mockedConsensusRequestsProcessor);

    protected override Hash256 ExpectedBlockHash => new("0x990d377b67dbffee4a60db6f189ae479ffb406e8abea16af55e0469b8524cf46");

    [TestCaseSource(nameof(GetWithdrawalValidationValues))]
    public override Task forkchoiceUpdatedV2_should_validate_withdrawals((IReleaseSpec Spec,
        string ErrorMessage,
        Withdrawal[]? Withdrawals,
        string BlockHash,
        int ErrorCode
        ) input)
        => base.forkchoiceUpdatedV2_should_validate_withdrawals(input);

    [TestCase(
        "0xe97d919a17fa5011ff3a08ffb07657ed9e1aaf5ff649888e5d7f605006caf598",
        "0xdd9be69fe6ed616f44d53576f430c1c7720ed0e7bff59478539a4a43dbb3bf1f",
        "0xd75d320c3a98a02ec7fe2abdcb1769bd063fec04d73f1735810f365ac12bc4ba",
        "0x3c6a8926870bdeff")]
    public override Task Should_process_block_as_expected_V4(string latestValidHash, string blockHash, string stateRoot, string payloadId)
        => base.Should_process_block_as_expected_V4(latestValidHash, blockHash, stateRoot, payloadId);

    [TestCase(
        "0xe168b70ac8a6f7d90734010030801fbb2dcce03a657155c4024b36ba8d1e3926",
        "0x3e604e45a9a74b66a7e03f828cc2597f0cb5f5e7dc50c9211be3a62fbcd6396d",
        "0xdbd87b98a6be7d4e3f11ff8500c38a0736d9a5e7a47b5cb25628d37187a98cb9",
        "0xcdd08163eccae523")]
    public override Task Should_process_block_as_expected_V2(string latestValidHash, string blockHash, string stateRoot, string payloadId)
        => base.Should_process_block_as_expected_V2(latestValidHash, blockHash, stateRoot, payloadId);

    [TestCase(
        "0xe4333fcde906675e50500bf53a6c73bc51b2517509bc3cff2d24d0de9b8dd23e",
        "0xe168b70ac8a6f7d90734010030801fbb2dcce03a657155c4024b36ba8d1e3926",
        "0xb22228e153345f9b")]
    public override Task processing_block_should_serialize_valid_responses(string blockHash, string latestValidHash, string payloadId)
        => base.processing_block_should_serialize_valid_responses(blockHash, latestValidHash, payloadId);

    [Test]
    [TestCase(
        "0xa66ec67b117f57388da53271f00c22a68e6c297b564f67c5904e6f2662881875",
        "0xe168b70ac8a6f7d90734010030801fbb2dcce03a657155c4024b36ba8d1e3926"
        )]
    [Parallelizable(ParallelScope.None)]
    [Obsolete]
    public override Task forkchoiceUpdatedV1_should_communicate_with_boost_relay_through_http(string blockHash, string parentHash)
        => base.forkchoiceUpdatedV1_should_communicate_with_boost_relay_through_http(blockHash, parentHash);

    [Ignore("Withdrawals are not withdrawan due to lack of Aura contract in tests")]
    public override Task Can_apply_withdrawals_correctly((Withdrawal[][] Withdrawals, (Address Account, UInt256 BalanceIncrease)[] ExpectedAccountIncrease) input)
    {
        return base.Can_apply_withdrawals_correctly(input);
    }

    public class MergeAuRaTestBlockchain : MergeTestBlockchain
    {
        private AuRaNethermindApi? _api;
        protected ITxSource? _additionalTxSource;

        public MergeAuRaTestBlockchain(IMergeConfig? mergeConfig = null, IPayloadPreparationService? mockedPayloadPreparationService = null, ITxSource? additionalTxSource = null, ILogManager? logManager = null, IConsensusRequestsProcessor? mockedConsensusRequestsProcessor = null)
            : base(mergeConfig, mockedPayloadPreparationService, logManager, mockedConsensusRequestsProcessor)
        {
            ConsensusRequestsProcessor = mockedConsensusRequestsProcessor;
            SealEngineType = Core.SealEngineType.AuRa;
            _additionalTxSource = additionalTxSource;
        }

        protected override Task<TestBlockchain> Build(ISpecProvider? specProvider = null, UInt256? initialValues = null, bool addBlockOnStart = true)
        {
            if (specProvider is TestSingleReleaseSpecProvider provider) provider.SealEngine = SealEngineType;
            return base.Build(specProvider, initialValues, addBlockOnStart);
        }

        protected override IBlockProcessor CreateBlockProcessor()
        {
            _api = new(new ConfigProvider(), new EthereumJsonSerializer(), LogManager,
                    new ChainSpec
                    {
                        AuRa = new()
                        {
                            WithdrawalContractAddress = new("0xbabe2bed00000000000000000000000000000003")
                        },
                        Parameters = new()
                    })
            {
                BlockTree = BlockTree,
                DbProvider = DbProvider,
                WorldStateManager = WorldStateManager,
                SpecProvider = SpecProvider,
                TransactionComparerProvider = TransactionComparerProvider,
                TxPool = TxPool
            };

            WithdrawalContractFactory withdrawalContractFactory = new(_api.ChainSpec!.AuRa, _api.AbiEncoder);
            WithdrawalProcessor = new AuraWithdrawalProcessor(
                    withdrawalContractFactory.Create(TxProcessor),
                    LogManager
            );

            BlockValidator = CreateBlockValidator();
            IBlockProcessor processor = new BlockProcessor(
                SpecProvider,
                BlockValidator,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, State),
                State,
                ReceiptStorage,
                TxProcessor,
                new BeaconBlockRootHandler(TxProcessor),
                new BlockhashStore(SpecProvider, State),
                LogManager,
                WithdrawalProcessor,
                consensusRequestsProcessor: ConsensusRequestsProcessor,
                preWarmer: CreateBlockCachePreWarmer());

            return new TestBlockProcessorInterceptor(processor, _blockProcessingThrottle);
        }


        protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
        {
            SealEngine = new MergeSealEngine(SealEngine, PoSSwitcher, SealValidator!, LogManager);
            BlocksConfig blocksConfig = new() { MinGasPrice = 0 };
            ISyncConfig syncConfig = new SyncConfig();
            TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(SpecProvider, blocksConfig);
            EthSyncingInfo = new EthSyncingInfo(BlockTree, ReceiptStorage, syncConfig,
                new StaticSelector(SyncMode.All), Substitute.For<ISyncProgressResolver>(), LogManager);
            PostMergeBlockProducerFactory blockProducerFactory = new(
                SpecProvider,
                SealEngine,
                Timestamper,
                blocksConfig,
                LogManager,
                targetAdjustedGasLimitCalculator);

            AuRaMergeBlockProducerEnvFactory blockProducerEnvFactory = new(
                _api!,
                WorldStateManager,
                BlockTree,
                SpecProvider,
                BlockValidator,
                NoBlockRewards.Instance,
                ReceiptStorage,
                BlockPreprocessorStep,
                TxPool,
                transactionComparerProvider,
                blocksConfig,
                LogManager,
                ConsensusRequestsProcessor);


            BlockProducerEnv blockProducerEnv = blockProducerEnvFactory.Create(_additionalTxSource);
            PostMergeBlockProducer postMergeBlockProducer = blockProducerFactory.Create(blockProducerEnv);
            PostMergeBlockProducer = postMergeBlockProducer;
            PayloadPreparationService ??= new PayloadPreparationService(
                postMergeBlockProducer,
                CreateBlockImprovementContextFactory(PostMergeBlockProducer),
                TimerFactory.Default,
                LogManager,
                TimeSpan.FromSeconds(MergeConfig.SecondsPerSlot),
                50000 // by default we want to avoid cleanup payload effects in testing
            );

            IAuRaStepCalculator auraStepCalculator = Substitute.For<IAuRaStepCalculator>();
            auraStepCalculator.TimeToNextStep.Returns(TimeSpan.FromMilliseconds(0));
            FollowOtherMiners gasLimitCalculator = new(MainnetSpecProvider.Instance);
            AuRaBlockProducer preMergeBlockProducer = new(
                txPoolTxSource,
                blockProducerEnvFactory.Create().ChainProcessor,
                State,
                sealer,
                BlockTree,
                Timestamper,
                auraStepCalculator,
                NullReportingValidator.Instance,
                new AuRaConfig(),
                gasLimitCalculator,
                SpecProvider,
                LogManager,
                blocksConfig
            );

            return new MergeBlockProducer(preMergeBlockProducer, postMergeBlockProducer, PoSSwitcher);
        }

        protected virtual IBlockImprovementContextFactory CreateBlockImprovementContextFactory(IBlockProducer blockProducer)
            => new BlockImprovementContextFactory(blockProducer, TimeSpan.FromSeconds(MergeConfig.SecondsPerSlot));
    }
}
