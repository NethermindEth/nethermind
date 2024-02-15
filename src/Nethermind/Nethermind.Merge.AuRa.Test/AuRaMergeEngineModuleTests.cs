// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.Merge.AuRa.Test;

public class AuRaMergeEngineModuleTests : EngineModuleTests
{
    private int _blocksProduced;
    private ExecutionPayload? _parentPayload;

    protected override MergeTestBlockchain CreateBaseBlockchain(
        IMergeConfig? mergeConfig = null,
        IPayloadPreparationService? mockedPayloadService = null,
        ILogManager? logManager = null)
        => new MergeAuRaTestBlockchain(mergeConfig, mockedPayloadService);

    protected override Hash256 ExpectedBlockHash => new("0x990d377b67dbffee4a60db6f189ae479ffb406e8abea16af55e0469b8524cf46");

    [TestCaseSource(nameof(GetWithdrawalValidationValues))]
    public override Task forkchoiceUpdatedV2_should_validate_withdrawals((IReleaseSpec Spec,
        string ErrorMessage,
        Withdrawal[]? Withdrawals,
        string BlockHash
        ) input)
        => base.forkchoiceUpdatedV2_should_validate_withdrawals(input);

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

    private async Task<ValueTuple<int, Transaction[]>> ProduceBlockWithTransactions(IEngineRpcModule rpc, MergeTestBlockchain chain, uint count)
    {
        int id = _blocksProduced;

        _parentPayload!.TryGetBlock(out Block? parentBlock);
        chain.AddTransactions(BuildTransactions(chain, parentBlock!.CalculateHash(), TestItem.PrivateKeyC, TestItem.AddressF, count, id, out _, out _));

        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 1, _parentPayload, true);
        executionPayloads.Should().HaveCount(1);

        _parentPayload = executionPayloads[0];
        _blocksProduced++;

        return (id, _parentPayload.GetTransactions());
    }

    [Test]
    public async Task Can_include_shutter_transactions()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(new TestSingleReleaseSpecProvider(London.Instance));
        IEngineRpcModule rpc = CreateEngineModule(chain);

        // creating chain with 3 blocks
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 3, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        _parentPayload = executionPayloads.Last();

        // should contain shutter transactions
        executionPayloads[0].GetTransactions().Should().HaveCount(1);
        executionPayloads[0].GetTransactions()[0].Value.Should().Be(123);

        executionPayloads[1].GetTransactions().Should().HaveCount(1);
        executionPayloads[1].GetTransactions()[0].Value.Should().Be(124);

        executionPayloads[2].GetTransactions().Should().HaveCount(1);
        executionPayloads[2].GetTransactions()[0].Value.Should().Be(125);

        // build blocks with transactions from both shutter and TxPool
        // id is used as value to differentiate between transactions
        (int id0, Transaction[] transactions0) = await ProduceBlockWithTransactions(rpc, chain, 3);
        transactions0.Should().HaveCount(4);
        transactions0[0].Value.Should().Be(126);
        transactions0[1].Value.Should().Be(id0.GWei());
        transactions0[2].Value.Should().Be(id0.GWei());
        transactions0[3].Value.Should().Be(id0.GWei());

        (int id1, Transaction[] transactions1) = await ProduceBlockWithTransactions(rpc, chain, 3);
        transactions1.Should().HaveCount(4);
        transactions1[0].Value.Should().Be(127);
        transactions1[1].Value.Should().Be(id1.GWei());
        transactions1[2].Value.Should().Be(id1.GWei());
        transactions1[3].Value.Should().Be(id1.GWei());

        (int id2, Transaction[] transactions2) = await ProduceBlockWithTransactions(rpc, chain, 1);
        transactions2.Should().HaveCount(2);
        transactions2[0].Value.Should().Be(128);
        transactions2[1].Value.Should().Be(id2.GWei());

        (int id3, Transaction[] transactions3) = await ProduceBlockWithTransactions(rpc, chain, 0);
        transactions3.Should().HaveCount(1);
        transactions3[0].Value.Should().Be(129);

        (int id4, Transaction[] transactions4) = await ProduceBlockWithTransactions(rpc, chain, 2);
        // bad shutter transaction excluded
        transactions4[0].Value.Should().Be(id4.GWei());
        transactions4[1].Value.Should().Be(id4.GWei());

        (int id5, Transaction[] transactions5) = await ProduceBlockWithTransactions(rpc, chain, 4);
        // bad shutter transaction excluded
        transactions5.Should().HaveCount(4);
        transactions5[0].Value.Should().Be(id5.GWei());
        transactions5[1].Value.Should().Be(id5.GWei());
        transactions5[2].Value.Should().Be(id5.GWei());
        transactions5[3].Value.Should().Be(id5.GWei());
    }

    class ShutterTxSource : ITxSource
    {
        private UInt256 _nonce = 0;
        private Transaction? _transaction;
        private Hash256? _lastParent;


        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
        {
            if (parent.Hash != _lastParent)
            {
                byte[] sigData = new byte[65];
                sigData[31] = 1; // correct r
                sigData[63] = 1; // correct s
                sigData[64] = 27;
                Signature signature = new(sigData);
                UInt256 value = 123 + _nonce;

                if (value == (UInt256)130)
                {
                    // bad transaction (incorrect nonce)
                    _transaction = Build.A.Transaction
                        .WithSenderAddress(TestItem.AddressA)
                        .WithValue(value)
                        .WithNonce(_nonce - 5)
                        .WithSignature(signature)
                        .TestObject;
                }
                else
                {
                    _transaction = Build.A.Transaction
                        .WithSenderAddress(TestItem.AddressA)
                        .WithValue(value)
                        .WithNonce(_nonce)
                        .WithSignature(signature)
                        .TestObject;
                }

                _nonce++;
            }

            _lastParent = parent.Hash;

            return new[] { _transaction! };
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
                new AuRaConfig(),
                new DisposableStack(),
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
