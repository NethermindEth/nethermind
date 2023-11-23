// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Shutter;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuRaMergeEngineModuleTests : EngineModuleTests
{
    protected override MergeTestBlockchain CreateBaseBlockchain(
        IMergeConfig? mergeConfig = null,
        IPayloadPreparationService? mockedPayloadService = null,
        ILogManager? logManager = null)
        => new MergeAuRaTestBlockchain(mergeConfig, mockedPayloadService);

    protected override Keccak ExpectedBlockHash => new("0x990d377b67dbffee4a60db6f189ae479ffb406e8abea16af55e0469b8524cf46");

    [TestCaseSource(nameof(GetWithdrawalValidationValues))]
    public override Task forkchoiceUpdatedV2_should_validate_withdrawals((IReleaseSpec Spec,
        string ErrorMessage,
        IEnumerable<Withdrawal>? Withdrawals,
        string BlockHash
        ) input)
        => base.forkchoiceUpdatedV2_should_validate_withdrawals(input);

    [TestCase(
        "0xe168b70ac8a6f7d90734010030801fbb2dcce03a657155c4024b36ba8d1e3926",
        "0x3e604e45a9a74b66a7e03f828cc2597f0cb5f5e7dc50c9211be3a62fbcd6396d",
        "0xdbd87b98a6be7d4e3f11ff8500c38a0736d9a5e7a47b5cb25628d37187a98cb9",
        "0x80ac487e132512b1")]
    public override Task Should_process_block_as_expected_V2(string latestValidHash, string blockHash, string stateRoot, string payloadId)
        => base.Should_process_block_as_expected_V2(latestValidHash, blockHash, stateRoot, payloadId);

    [TestCase(
        "0xe4333fcde906675e50500bf53a6c73bc51b2517509bc3cff2d24d0de9b8dd23e",
        "0xe168b70ac8a6f7d90734010030801fbb2dcce03a657155c4024b36ba8d1e3926",
        "0x78ecfec08729d895")]
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

    [Test]
    public async Task Should_build_shuttered_block()
    {
        //using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockchain(new TestSingleReleaseSpecProvider(London.Instance));
        IEngineRpcModule rpc = CreateEngineModule(chain);

        // creating chain with 30 blocks
        await ProduceBranchV1(rpc, chain, 30, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        TimeSpan delay = TimeSpan.FromMilliseconds(10);
        TimeSpan timePerSlot = 4 * delay;
        StoringBlockImprovementContextFactory improvementContextFactory = new(new BlockImprovementContextFactory(chain.BlockProductionTrigger, TimeSpan.FromSeconds(chain.MergeConfig.SecondsPerSlot)));
        chain.PayloadPreparationService = new PayloadPreparationService(
            chain.PostMergeBlockProducer!,
            improvementContextFactory,
            TimerFactory.Default,
            chain.LogManager,
            timePerSlot);
        //chain.PayloadPreparationService!.BlockImproved += (_, _) => { blockImprovementLock.Release(1); };

        Block block30 = chain.BlockTree.Head!;

        // we added transactions
        chain.AddTransactions(BuildTransactions(chain, block30.CalculateHash(), TestItem.PrivateKeyB, TestItem.AddressF, 3, 10, out _, out _));
        PayloadAttributes payloadAttributesBlock31A = new PayloadAttributes
        {
            Timestamp = (ulong)DateTime.UtcNow.AddDays(3).Ticks,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = Address.Zero
        };
        string? payloadIdBlock31A = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(block30.GetOrCalculateHash(), Keccak.Zero, block30.GetOrCalculateHash()),
                payloadAttributesBlock31A)
            .Result.Data.PayloadId!;
        //await blockImprovementLock.WaitAsync(delay * 1000);

        ExecutionPayload getPayloadResultBlock31A = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadIdBlock31A))).Data!;
        getPayloadResultBlock31A.Should().NotBeNull();
        await rpc.engine_newPayloadV1(getPayloadResultBlock31A);

        // current main chain block 30->31A, we start building payload 32A
        await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(getPayloadResultBlock31A.BlockHash, Keccak.Zero, getPayloadResultBlock31A.BlockHash));
        Block block31A = chain.BlockTree.Head!;
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = (ulong)DateTime.UtcNow.AddDays(4).Ticks,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = Address.Zero
        };

        // we build one more block on the same level
        Block block31B = chain.PostMergeBlockProducer!.PrepareEmptyBlock(block30.Header, payloadAttributes);
        await rpc.engine_newPayloadV1(new ExecutionPayload(block31B));

        // ...and we change the main chain, so main chain now is 30->31B, block improvement for block 32A is still in progress
        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(block31A.GetOrCalculateHash(), Keccak.Zero, block31A.GetOrCalculateHash()),
                payloadAttributes)
            .Result.Data.PayloadId!;
        ForkchoiceUpdatedV1Result result = rpc.engine_forkchoiceUpdatedV1(
            new ForkchoiceStateV1(block31B.GetOrCalculateHash(), Keccak.Zero, block31B.GetOrCalculateHash())).Result.Data;

        // we added same transactions, so if we build on incorrect state root, we will end up with invalid block
        chain.AddTransactions(BuildTransactions(chain, block31A.GetOrCalculateHash(), TestItem.PrivateKeyC, TestItem.AddressF, 3, 10, out _, out _));
        //await blockImprovementLock.WaitAsync(delay * 1000);
        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;
        getPayloadResult.Should().NotBeNull();

        ResultWrapper<PayloadStatusV1> finalResult = await rpc.engine_newPayloadV1(getPayloadResult);
        finalResult.Data.Status.Should().Be(PayloadStatus.Valid);
    }

    class ShutterTxSource : ITxSource
    {
        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            //return Enumerable.Empty<Transaction>();
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 27;
            Signature signature = new(sigData);

            return new[] {
                Build.A.Transaction
                .WithSenderAddress(TestItem.AddressA)
                .WithValue(123)
                .WithNonce(0)
                .WithSignature(signature)
                .TestObject,
                Build.A.Transaction
                .WithSenderAddress(TestItem.AddressA)
                .WithValue(456)
                .WithNonce(1)
                .WithSignature(signature)
                .TestObject
            };
        }
    }

    class MergeAuRaTestBlockchain : MergeTestBlockchain
    {
        private AuRaNethermindApi? _api;

        public MergeAuRaTestBlockchain(IMergeConfig? mergeConfig = null, IPayloadPreparationService? mockedPayloadPreparationService = null)
            : base(mergeConfig, mockedPayloadPreparationService)
        {
            SealEngineType = Core.SealEngineType.AuRa;
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
                ReadOnlyTrieStore = ReadOnlyTrieStore,
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
                NullWitnessCollector.Instance,
                LogManager,
                WithdrawalProcessor);

            return new TestBlockProcessorInterceptor(processor, _blockProcessingThrottle);
        }


        protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
        {
            SealEngine = new MergeSealEngine(SealEngine, PoSSwitcher, SealValidator!, LogManager);
            BlocksConfig blocksConfig = new() { MinGasPrice = 0 };
            ISyncConfig syncConfig = new SyncConfig();
            TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(SpecProvider, blocksConfig);
            EthSyncingInfo = new EthSyncingInfo(BlockTree, ReceiptStorage, syncConfig, new StaticSelector(SyncMode.All), LogManager);
            PostMergeBlockProducerFactory blockProducerFactory = new(
                SpecProvider,
                SealEngine,
                Timestamper,
                blocksConfig,
                LogManager,
                targetAdjustedGasLimitCalculator);

            AuRaMergeBlockProducerEnvFactory blockProducerEnvFactory = new(
                _api!,
                new AuRaConfig(),
                new DisposableStack(),
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
                blocksConfig,
                LogManager);


            BlockProducerEnv blockProducerEnv = blockProducerEnvFactory.Create(new ShutterTxSource());
            PostMergeBlockProducer postMergeBlockProducer = blockProducerFactory.Create(blockProducerEnv, BlockProductionTrigger);
            PostMergeBlockProducer = postMergeBlockProducer;
            PayloadPreparationService ??= new PayloadPreparationService(
                postMergeBlockProducer,
                new BlockImprovementContextFactory(BlockProductionTrigger, TimeSpan.FromSeconds(MergeConfig.SecondsPerSlot)
                ),
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
                BlockProductionTrigger,
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
    }
}
