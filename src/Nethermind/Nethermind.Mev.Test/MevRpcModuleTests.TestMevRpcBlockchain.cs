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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
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
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.Runner.Ethereum;
using Nethermind.State;
using Newtonsoft.Json;
using NLog.Fluent;
using NSubstitute;
using Org.BouncyCastle.Asn1.Cms;

namespace Nethermind.Mev.Test
{
    public partial class MevRpcModuleTests
    {
        private Task<TestMevRpcBlockchain> CreateChain(SelectorType selectorType, int? maxMergedBundles = 10, Func<ISimulatedBundleSource, IBundleSource>? getSelector = null)
        {
            switch (selectorType)
            {
                case SelectorType.V1:
                    getSelector ??= source => new V1Selector(source);
                    break;
                case SelectorType.V2:
                    getSelector ??= source => new V2Selector(source, (int) maxMergedBundles!);
                    break;
                default:
                    throw new NotImplementedException("Only V1 and V2 are supported");
            }
            TestMevRpcBlockchain testMevRpcBlockchain = new(getSelector);
            return TestRpcBlockchain.ForTest(testMevRpcBlockchain).Build();
        }
        
        private class TestMevRpcBlockchain : TestRpcBlockchain
        {
            private readonly Func<ISimulatedBundleSource, IBundleSource> _getSelector;
            private ITracerFactory _tracerFactory = null!;
            public BundlePool BundlePool { get; private set; } = null!;

            public TestMevRpcBlockchain(Func<ISimulatedBundleSource, IBundleSource> getSelector)
            {
                _getSelector = getSelector;
                Signer = new Eth2Signer(MinerAddress);
                GenesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.Genesis
                    .WithTimestamp(UInt256.One)
                    .WithGasLimit(GasLimitCalculator.GasLimit);
            }
            
            public IMevRpcModule MevRpcModule { get; set; } = Substitute.For<IMevRpcModule>();
            public IManualBlockFinalizationManager FinalizationManager { get; } = new ManualBlockFinalizationManager();
            public ManualGasLimitCalculator GasLimitCalculator = new() {GasLimit = 10_000_000};
            private MevConfig _mevConfig = new MevConfig {Enabled = true};
            public Address MinerAddress => TestItem.PrivateKeyD.Address;
            private IBlockValidator BlockValidator { get; set; } = null!;
            private ISigner Signer { get; }

            public override ILogManager LogManager => NUnitLogManager.Instance;

            protected override ITestBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, BlockchainProcessor chainProcessor, IStateProvider producerStateProvider, ISealer sealer)
            {
                MiningConfig miningConfig = new() {MinGasPrice = UInt256.One};
                
                MevBlockProducerEnvFactory blockProducerEnvFactory = new MevBlockProducerEnvFactory(
                    DbProvider, 
                    BlockTree, 
                    ReadOnlyTrieStore, 
                    SpecProvider, 
                    BlockValidator,
                    NoBlockRewards.Instance,
                    ReceiptStorage,
                    BlockPreprocessorStep,
                    TxPool,
                    miningConfig,
                    LogManager);

                Eth2BlockProducer CreateEth2BlockProducer(ITxSource? txSource = null) =>
                    new Eth2TestBlockProducerFactory(GasLimitCalculator, txSource).Create(
                        blockProducerEnvFactory,
                        BlockTree,
                        BlockProcessingQueue,
                        SpecProvider,
                        Signer,
                        Timestamper,
                        miningConfig,
                        LogManager);

                IBundleSource bundleSource = _getSelector(BundlePool);
                BundleTxSource bundleTxSource = new(bundleSource, Timestamper);
                
                IManualBlockProducer standardProducer = CreateEth2BlockProducer();
                IBeneficiaryBalanceSource standardProducerBeneficiaryBalanceSource = blockProducerEnvFactory.LastMevBlockProcessor;
                IManualBlockProducer bundleProducer = CreateEth2BlockProducer(bundleTxSource);
                IBeneficiaryBalanceSource bundleProducerBeneficiaryBalanceSource = blockProducerEnvFactory.LastMevBlockProcessor;
                
                return new MevTestBlockProducer(BlockTree, new Dictionary<IManualBlockProducer, IBeneficiaryBalanceSource>()
                {
                    {bundleProducer, bundleProducerBeneficiaryBalanceSource}, 
                    {standardProducer, standardProducerBeneficiaryBalanceSource}
                });
            }

            protected override BlockProcessor CreateBlockProcessor()
            {
                BlockValidator = CreateBlockValidator();
                BlockProcessor blockProcessor = new(
                    SpecProvider,
                    BlockValidator,
                    NoBlockRewards.Instance,
                    TxProcessor,
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
                
                TxBundleSimulator txBundleSimulator = new(_tracerFactory, FollowOtherMiners.Instance, Timestamper, TxPool, FinalizationManager);
                BundlePool = new BundlePool(BlockTree, txBundleSimulator, FinalizationManager, Timestamper, _mevConfig, LogManager);

                return blockProcessor;
            }

            protected override async Task<TestBlockchain> Build(ISpecProvider specProvider = null, UInt256? initialValues = null)
            {
                TestBlockchain chain = await base.Build(specProvider, initialValues);
                MevRpcModule = new MevRpcModule(
                    _mevConfig,
                    new JsonRpcConfig(),
                    BundlePool,
                    BlockFinder,
                    StateReader,
                    _tracerFactory,
                    SpecProvider.ChainId);
                
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

            internal class MevTestBlockProducer : MevBlockProducer, ITestBlockProducer
            {
                private readonly IBlockTree _blockTree;
                private Block? _lastProducedBlock;
                
                public MevTestBlockProducer(IBlockTree blockTree, IDictionary<IManualBlockProducer, IBeneficiaryBalanceSource> blockProducers) : base(blockProducers)
                {
                    _blockTree = blockTree;
                }
        
                public Block? LastProducedBlock
                {
                    get
                    {
                        return _lastProducedBlock!;
                    }
                    private set
                    {
                        _lastProducedBlock = value;
                        if (value != null)
                        {
                            LastProducedBlockChanged?.Invoke(this, new BlockEventArgs(value));
                        }
                    }
                }

                public event EventHandler<BlockEventArgs> LastProducedBlockChanged = null!;
        
                public async Task<bool> BuildNewBlock()
                {
                    Block? block = await TryProduceBlock(_blockTree.Head!.Header, CancellationToken.None);
                    if (block is not null)
                    {
                        _blockTree.SuggestBlock(block);
                        return true;
                    }

                    return false;
                }
            }
        }
    }
}
