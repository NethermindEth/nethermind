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
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
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
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.Synchronization;
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
        IExecutionRequestsProcessor? mockedExecutionRequestsProcessor = null)
        => new MergeAuRaTestBlockchain(mergeConfig, mockedPayloadService, null, logManager, mockedExecutionRequestsProcessor);

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
        "0xe05eb5783e1ee8b0b6a6c4c9a09a9bdeb798835d3d44cda510e3083c0578165f",
        "0xd75d320c3a98a02ec7fe2abdcb1769bd063fec04d73f1735810f365ac12bc4ba")]
    public override Task NewPayloadV5_should_reject_block_with_unsatisfied_inclusion_list_V5(string blockHash, string stateRoot)
        => base.NewPayloadV5_should_reject_block_with_unsatisfied_inclusion_list_V5(blockHash, stateRoot);

    [TestCase(
        "0x1270af16dfea9b40aa9381529cb2629008fea35386041f52c07034ea8c038a05",
        "0x81a41f22fa776446737cc3dfab96f8536bacfa2fd3d85b0f013b55a6be3ecfe7",
        "0x6d43db6fab328470c4ad01d6658a317496d373a1892aab8273bf52448beb915e",
        "0xba04b196bf0014df",
        "0x642cd2bcdba228efb3996bf53981250d3608289522b80754c4e3c085c93c806f",
        "0x2632e314a000",
        "0x5208")]
    public override Task Should_build_block_with_inclusion_list_transactions_V5(
        string latestValidHash,
        string blockHash,
        string stateRoot,
        string payloadId,
        string receiptsRoot,
        string blockFees,
        string gasUsed)
        => base.Should_build_block_with_inclusion_list_transactions_V5(latestValidHash, blockHash, stateRoot, payloadId, receiptsRoot, blockFees, gasUsed);

    [TestCase(
        "0x1270af16dfea9b40aa9381529cb2629008fea35386041f52c07034ea8c038a05",
        "0xea3bdca86662fa8b5399f2c3ff494ced747f07834740ead723ebe023852e9ea1",
        "0xd75d320c3a98a02ec7fe2abdcb1769bd063fec04d73f1735810f365ac12bc4ba",
        "0x6c8286694756e470")]
    public override Task Should_process_block_as_expected_V5(string latestValidHash, string blockHash, string stateRoot, string payloadId)
        => base.Should_process_block_as_expected_V5(latestValidHash, blockHash, stateRoot, payloadId);

    [TestCase(
        "0x1270af16dfea9b40aa9381529cb2629008fea35386041f52c07034ea8c038a05",
        "0xea3bdca86662fa8b5399f2c3ff494ced747f07834740ead723ebe023852e9ea1",
        "0xd75d320c3a98a02ec7fe2abdcb1769bd063fec04d73f1735810f365ac12bc4ba",
        "0x7389011914b1ca84")]
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

        public MergeAuRaTestBlockchain(IMergeConfig? mergeConfig = null, IPayloadPreparationService? mockedPayloadPreparationService = null, ITxSource? additionalTxSource = null, ILogManager? logManager = null, IExecutionRequestsProcessor? mockedExecutionRequestsProcessor = null)
            : base(mergeConfig, mockedPayloadPreparationService, logManager, mockedExecutionRequestsProcessor)
        {
            ExecutionRequestsProcessor = mockedExecutionRequestsProcessor;
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
                        EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                            new AuRaChainSpecEngineParameters
                            {
                                WithdrawalContractAddress = new("0xbabe2bed00000000000000000000000000000003")
                            }),
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

            WithdrawalContractFactory withdrawalContractFactory = new(_api.ChainSpec!.EngineChainSpecParametersProvider
                .GetChainSpecParameters<AuRaChainSpecEngineParameters>(), _api.AbiEncoder);
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
                new BeaconBlockRootHandler(TxProcessor, State),
                new BlockhashStore(SpecProvider, State),
                LogManager,
                WithdrawalProcessor,
                executionRequestsProcessor: ExecutionRequestsProcessor,
                preWarmer: CreateBlockCachePreWarmer());

            return new TestBlockProcessorInterceptor(processor, _blockProcessingThrottle);
        }


        protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
        {
            SealEngine = new MergeSealEngine(SealEngine, PoSSwitcher, SealValidator!, LogManager);
            BlocksConfig blocksConfig = new() { MinGasPrice = 0 };
            ISyncConfig syncConfig = new SyncConfig();
            TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(SpecProvider, blocksConfig);
            EthSyncingInfo = new EthSyncingInfo(BlockTree, Substitute.For<ISyncPointers>(), syncConfig,
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
                ExecutionRequestsProcessor);


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
