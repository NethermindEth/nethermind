// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Test;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;

namespace Nethermind.Mev.Test
{
    public partial class MevRpcModuleTests
    {
        public static Task<TestMevRpcBlockchain> CreateChain(int maxMergedBundles, IReleaseSpec? releaseSpec = null,
            UInt256? initialBaseFeePerGas = null, Address[]? relayAddresses = null)
        {
            TestMevRpcBlockchain testMevRpcBlockchain = new(maxMergedBundles, initialBaseFeePerGas, relayAddresses);
            TestSpecProvider testSpecProvider = releaseSpec is not null
                ? new TestSpecProvider(releaseSpec)
                : new TestSpecProvider(Berlin.Instance);
            return TestRpcBlockchain.ForTest(testMevRpcBlockchain).Build(testSpecProvider);
        }

        public class TestMevRpcBlockchain : TestRpcBlockchain
        {
            private readonly int _maxMergedBundles;
            private readonly Address[] _relayAddresses;

            private ITracerFactory _tracerFactory = null!;
            public TestBundlePool BundlePool { get; private set; } = null!;

            private readonly MevConfig _mevConfig;

            public TestMevRpcBlockchain(int maxMergedBundles, UInt256? initialBaseFeePerGas, Address[]? relayAddresses)
            {
                _maxMergedBundles = maxMergedBundles;
                _relayAddresses = relayAddresses ?? Array.Empty<Address>();
                _mevConfig = new MevConfig
                {
                    Enabled = true,
                    TrustedRelays = string.Join(",", _relayAddresses.ToList()),
                    MaxMergedBundles = _maxMergedBundles
                };
                Signer = new TestMevSigner(MinerAddress);
                GenesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.Genesis
                    .WithTimestamp(1UL)
                    .WithGasLimit(GasLimitCalculator.GasLimit)
                    .WithBaseFeePerGas(initialBaseFeePerGas ?? 0);
            }

            public IMevRpcModule MevRpcModule { get; set; } = Substitute.For<IMevRpcModule>();
            public ManualGasLimitCalculator GasLimitCalculator = new() { GasLimit = 10_000_000 };

            public Address MinerAddress => TestItem.PrivateKeyD.Address;
            private ISigner Signer { get; }

            public override ILogManager LogManager => LimboLogs.Instance;

            protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer,
                ITransactionComparerProvider transactionComparerProvider)
            {
                BlocksConfig blocksConfig = new() { MinGasPrice = UInt256.One };
                SpecProvider.UpdateMergeTransitionInfo(1, 0);

                BlockProducerEnvFactory blockProducerEnvFactory = new(
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
                    LogManager)
                {
                    TransactionsExecutorFactory =
                        new MevBlockProducerTransactionsExecutorFactory(SpecProvider, LogManager)
                };

                PostMergeBlockProducer CreatePostMergeBlockProducer(ITxSource? txSource = null)
                {
                    BlockProducerEnv blockProducerEnv = blockProducerEnvFactory.Create(txSource);
                    return new PostMergeBlockProducerFactory(SpecProvider, SealEngine, Timestamper, blocksConfig,
                        LogManager).Create(blockProducerEnv, txSource);
                }

                MevBlockProducer.MevBlockProducerInfo CreateProducer(int bundleLimit = 0,
                    ITxSource? additionalTxSource = null)
                {
                    IBlockProductionCondition condition = bundleLimit == 0
                        ? AlwaysOkBlockProductionCondition.Instance
                        : new TestBundleLimitBlockProductionTrigger(BlockTree, BundlePool, bundleLimit);

                    IBlockProducer producer = CreatePostMergeBlockProducer(additionalTxSource);
                    return new MevBlockProducer.MevBlockProducerInfo(producer, condition, new BeneficiaryTracer());
                }

                int megabundleProducerCount = _relayAddresses.Length != 0 ? 1 : 0;
                List<MevBlockProducer.MevBlockProducerInfo> blockProducers =
                    new(_maxMergedBundles + megabundleProducerCount + 1);

                // Add non-mev block
                MevBlockProducer.MevBlockProducerInfo standardProducer = CreateProducer();
                blockProducers.Add(standardProducer);

                // Try blocks with all bundle numbers <= maxMergedBundles
                for (int bundleLimit = 1; bundleLimit <= _maxMergedBundles; bundleLimit++)
                {
                    BundleSelector bundleSelector = new(BundlePool, bundleLimit);
                    BundleTxSource bundleTxSource = new(bundleSelector, Timestamper);
                    MevBlockProducer.MevBlockProducerInfo bundleProducer = CreateProducer(bundleLimit, bundleTxSource);
                    blockProducers.Add(bundleProducer);
                }

                if (megabundleProducerCount > 0)
                {
                    MegabundleSelector megabundleSelector = new(BundlePool);
                    BundleTxSource megabundleTxSource = new(megabundleSelector, Timestamper);
                    MevBlockProducer.MevBlockProducerInfo bundleProducer = CreateProducer(0, megabundleTxSource);
                    blockProducers.Add(bundleProducer);
                }

                MevBlockProducer blockProducer = new MevBlockProducer(LogManager, blockProducers.ToArray());
                return blockProducer;
            }

            protected override IBlockProducerRunner CreateBlockProducerRunner()
            {
                IBlockProducerRunner baseRunner = base.CreateBlockProducerRunner();
                baseRunner.BlockProduced += OnBlockProduced;
                return baseRunner;
            }

            private void OnBlockProduced(object? sender, BlockEventArgs e)
            {
                BlockTree.SuggestBlock(e.Block, BlockTreeSuggestOptions.ForceDontSetAsMain);
                BlockchainProcessor.Process(e.Block!, GetProcessingOptions(), NullBlockTracer.Instance);
                BlockTree.UpdateMainChain(new[] { e.Block! }, true);
            }

            private ProcessingOptions GetProcessingOptions()
            {
                ProcessingOptions options = ProcessingOptions.None;
                options |= ProcessingOptions.StoreReceipts;
                return options;
            }

            protected override BlockProcessor CreateBlockProcessor()
            {
                BlockValidator = CreateBlockValidator();
                BlockProcessor blockProcessor = new(
                    SpecProvider,
                    BlockValidator,
                    NoBlockRewards.Instance,
                    new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, State),
                    State,
                    ReceiptStorage,
                    new BlockhashStore(SpecProvider, State),
                    LogManager);

                _tracerFactory = new TracerFactory(
                    BlockTree,
                    WorldStateManager,
                    BlockPreprocessorStep,
                    SpecProvider,
                    LogManager,
                    ProcessingOptions.ProducingBlock);

                TxBundleSimulator txBundleSimulator = new(_tracerFactory, GasLimitCalculator, Timestamper, TxPool,
                    SpecProvider, Signer);
                BundlePool = new TestBundlePool(BlockTree, txBundleSimulator, Timestamper,
                    new TxValidator(BlockTree.ChainId), SpecProvider, _mevConfig, LogManager, EthereumEcdsa);

                return blockProcessor;
            }

            protected override async Task<TestBlockchain> Build(ISpecProvider? specProvider = null,
                UInt256? initialValues = null, bool addBlockOnStart = true)
            {
                TestBlockchain chain = await base.Build(specProvider, initialValues);
                MevRpcModule = new MevRpcModule(new JsonRpcConfig(),
                    BundlePool,
                    BlockFinder,
                    StateReader,
                    _tracerFactory,
                    SpecProvider,
                    Signer);

                return chain;
            }

            private IBlockValidator CreateBlockValidator()
            {
                HeaderValidator headerValidator = new(BlockTree, Always.Valid, SpecProvider, LogManager);

                return new BlockValidator(
                    new TxValidator(SpecProvider.ChainId),
                    headerValidator,
                    Always.Valid,
                    SpecProvider,
                    LogManager);
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;

            public MevBundle SendBundle(int blockNumber, params BundleTransaction[] txs)
            {
                byte[][] bundleBytes = txs.Select(t => Rlp.Encode(t, RlpBehaviors.SkipTypedWrapping).Bytes).ToArray();
                Hash256[] revertingTxHashes = txs.Where(t => t.CanRevert).Select(t => t.Hash!).ToArray();
                MevBundleRpc mevBundleRpc = new()
                {
                    BlockNumber = blockNumber,
                    Txs = bundleBytes,
                    RevertingTxHashes = revertingTxHashes
                };
                ResultWrapper<bool> resultOfBundle = MevRpcModule.eth_sendBundle(mevBundleRpc);

                resultOfBundle.Result.Should().NotBe(Result.Success);
                resultOfBundle.Data.Should().Be(true);
                return new MevBundle(blockNumber, txs);
            }
        }

        private class TestBundleLimitBlockProductionTrigger(
            IBlockTree blockTree,
            IBundlePool bundlePool,
            int bundleLimit
        ) : IBlockProductionCondition
        {

            // TODO: this could be simplified a lot of the parent was not retrieved, not sure why do we need the parent here
            public bool CanProduce(BlockHeader parentHeader)
            {
                // TODO: why do we need this parent? later we use only the current block number
                BlockHeader? parent = blockTree.GetProducedBlockParent(parentHeader);
                if (parent is not null)
                {
                    // ToDo resolved conflict parent.Timestamp?
                    IEnumerable<MevBundle> bundles = bundlePool.GetBundles(parent.Number + 1, parent.Timestamp);
                    return bundles.Count() >= bundleLimit;
                }

                return false;
            }
        }
    }
}
