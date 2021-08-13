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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.Runner.Ethereum;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Newtonsoft.Json;
using NLog.Fluent;
using NSubstitute;
using Org.BouncyCastle.Asn1.Cms;

namespace Nethermind.Mev.Test
{
    public partial class MevRpcModuleTests
    {
        public static Task<TestMevRpcBlockchain> CreateChain(int maxMergedBundles, IReleaseSpec? releaseSpec = null, UInt256? initialBaseFeePerGas = null)
        {
            TestMevRpcBlockchain testMevRpcBlockchain = new(maxMergedBundles, initialBaseFeePerGas);
            TestSpecProvider testSpecProvider = releaseSpec is not null ? new TestSpecProvider(releaseSpec) : new TestSpecProvider(Berlin.Instance);
            testSpecProvider.ChainId = 1;
            return TestRpcBlockchain.ForTest(testMevRpcBlockchain).Build(testSpecProvider);
        }

        public class TestMevRpcBlockchain : TestRpcBlockchain
        {
            private readonly int _maxMergedBundles;
            
            private ITracerFactory _tracerFactory = null!;
            public TestBundlePool BundlePool { get; private set; } = null!;

            public TestMevRpcBlockchain(int maxMergedBundles, UInt256? initialBaseFeePerGas)
            {
                _maxMergedBundles = maxMergedBundles;
                Signer = new Eth2Signer(MinerAddress);
                GenesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.Genesis
                    .WithTimestamp(UInt256.One)
                    .WithGasLimit(GasLimitCalculator.GasLimit)
                    .WithBaseFeePerGas(initialBaseFeePerGas ?? 0);
            }
            
            public IMevRpcModule MevRpcModule { get; set; } = Substitute.For<IMevRpcModule>();
            public ManualGasLimitCalculator GasLimitCalculator = new() {GasLimit = 10_000_000};
            private MevConfig _mevConfig = new MevConfig {Enabled = true};
            public Address MinerAddress => TestItem.PrivateKeyD.Address;
            private IBlockValidator BlockValidator { get; set; } = null!;
            private ISigner Signer { get; }

            public override ILogManager LogManager => NUnitLogManager.Instance;

            protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
            {
                MiningConfig miningConfig = new() {MinGasPrice = UInt256.One};
                
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
                    LogManager)
                {
                    TransactionsExecutorFactory = new MevBlockProducerTransactionsExecutorFactory(SpecProvider, LogManager)
                };

                Eth2BlockProducer CreateEth2BlockProducer(IBlockProductionTrigger blockProductionTrigger, ITxSource? txSource = null) =>
                    new Eth2TestBlockProducerFactory(GasLimitCalculator, txSource).Create(
                        blockProducerEnvFactory,
                        BlockTree,
                        blockProductionTrigger,
                        SpecProvider,
                        Signer,
                        Timestamper,
                        miningConfig,
                        LogManager);
                
                MevBlockProducer.MevBlockProducerInfo CreateProducer(ITxSource? additionalTxSource = null)
                {
                    IManualBlockProductionTrigger trigger = new BuildBlocksWhenRequested();
                    IBlockProducer producer = CreateEth2BlockProducer(trigger, additionalTxSource);
                    return new MevBlockProducer.MevBlockProducerInfo(producer, trigger, new BeneficiaryTracer());
                }

                List<MevBlockProducer.MevBlockProducerInfo> blockProducers =
                    new(_maxMergedBundles + 1);
                    
                // Add non-mev block
                MevBlockProducer.MevBlockProducerInfo standardProducer = CreateProducer();
                blockProducers.Add(standardProducer);

                // Try blocks with all bundle numbers <= maxMergedBundles
                for (int bundleLimit = 1; bundleLimit <= _maxMergedBundles; bundleLimit++)
                {
                    BundleSelector bundleSelector = new(BundlePool, bundleLimit);
                    BundleTxSource bundleTxSource = new(bundleSelector, Timestamper);
                    MevBlockProducer.MevBlockProducerInfo bundleProducer = CreateProducer(bundleTxSource);
                    blockProducers.Add(bundleProducer);
                }

                return new MevBlockProducer(BlockProductionTrigger, LogManager, blockProducers.ToArray());
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
                    Storage,
                    ReceiptStorage,
                    NullWitnessCollector.Instance,
                    LogManager);
                
                _tracerFactory = new TracerFactory(
                    DbProvider, 
                    BlockTree, 
                    ReadOnlyTrieStore, 
                    BlockPreprocessorStep, 
                    SpecProvider, 
                    LogManager,
                    ProcessingOptions.ProducingBlock);
                
                TxBundleSimulator txBundleSimulator = new(_tracerFactory, GasLimitCalculator, Timestamper, TxPool, SpecProvider, Signer);
                BundlePool = new TestBundlePool(BlockTree, txBundleSimulator, Timestamper, new TxValidator(BlockTree.ChainId), SpecProvider, _mevConfig, LogManager);

                return blockProcessor;
            }

            protected override async Task<TestBlockchain> Build(ISpecProvider specProvider = null, UInt256? initialValues = null)
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
                HeaderValidator headerValidator = new(BlockTree, new Eth2SealEngine(Signer), SpecProvider, LogManager);
                
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
                byte[][] bundleBytes = txs.Select(t => Rlp.Encode(t).Bytes).ToArray();
                Keccak[] revertingTxHashes = txs.Where(t => t.CanRevert).Select(t => t.Hash!).ToArray();
                MevBundleRpc mevBundleRpc = new() {BlockNumber = blockNumber, Txs = bundleBytes, RevertingTxHashes = revertingTxHashes};
                ResultWrapper<bool> resultOfBundle = MevRpcModule.eth_sendBundle(mevBundleRpc);
                resultOfBundle.GetResult().ResultType.Should().NotBe(ResultType.Failure);
                resultOfBundle.GetData().Should().Be(true);
                return new MevBundle(blockNumber, txs);
            }
        }
    }
}
