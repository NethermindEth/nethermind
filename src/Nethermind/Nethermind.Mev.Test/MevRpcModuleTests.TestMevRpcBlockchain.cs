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
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.State;
using Newtonsoft.Json;
using NSubstitute;

namespace Nethermind.Mev.Test
{
    public partial class MevRpcModuleTests
    {
        private Task<TestMevRpcBlockchain> CreateChain(Func<ISimulatedBundleSource, IBundleSource>? getSelector = null)
        {
            getSelector ??= source => new V1Selector(source);
            TestMevRpcBlockchain testMevRpcBlockchain = new TestMevRpcBlockchain(getSelector);
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
                    .WithGasLimit(12_000_000);
            }
            
            public IMevRpcModule MevRpcModule { get; set; } = Substitute.For<IMevRpcModule>();
            public IManualBlockFinalizationManager FinalizationManager { get; } = new ManualBlockFinalizationManager();
            public ManualTimestamper ManualTimestamper { get; } = new();
            public Address MinerAddress => TestItem.PrivateKeyA.Address;
            private IBlockValidator BlockValidator { get; set; } = null!;
            private ISigner Signer { get; }
            
            protected override ITestBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, BlockchainProcessor chainProcessor, IStateProvider producerStateProvider, ISealer sealer)
            {
                MiningConfig miningConfig = new() {MinGasPrice = UInt256.One};

                Eth2BlockProducer CreateEth2BlockProducer(ITxSource txSource = null)
                {
                    return new Eth2TestBlockProducerFactory(txSource).Create(
                        BlockTree,
                        DbProvider,
                        ReadOnlyTrieStore,
                        BlockPreprocessorStep,
                        TxPool,
                        BlockValidator,
                        NoBlockRewards.Instance,
                        ReceiptStorage,
                        BlockProcessingQueue,
                        SpecProvider,
                        Signer,
                        Timestamper,
                        miningConfig,
                        LogManager);
                }

                IBundleSource bundleSource = _getSelector(BundlePool);
                BundleTxSource bundleTxSource = new(bundleSource, ManualTimestamper);
                return new MevTestBlockProducer(BlockTree, CreateEth2BlockProducer(bundleTxSource), CreateEth2BlockProducer());
            }

            protected override BlockProcessor CreateBlockProcessor()
            {
                BlockValidator = CreateBlockValidator();
                BlockProcessor blockProcessor = base.CreateBlockProcessor();
                
                _tracerFactory = new TracerFactory(DbProvider, BlockTree, ReadOnlyTrieStore, BlockPreprocessorStep, SpecProvider, LogManager);
                TxBundleSimulator txBundleSimulator = new(_tracerFactory, FollowOtherMiners.Instance, ManualTimestamper);
                BundlePool = new BundlePool(BlockTree, txBundleSimulator, FinalizationManager);

                return blockProcessor;
            }

            protected override async Task<TestBlockchain> Build(ISpecProvider specProvider = null, UInt256? initialValues = null)
            {
                TestBlockchain build = await base.Build(specProvider, initialValues);
                MevRpcModule = new MevRpcModule(
                    new MevConfig {Enabled = true},
                    new JsonRpcConfig(),
                    BundlePool,
                    BlockFinder,
                    StateReader,
                    _tracerFactory,
                    SpecProvider.ChainId);
                
                return build;
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
                
                public MevTestBlockProducer(IBlockTree blockTree, params IManualBlockProducer[] blockProducers) : base(blockProducers)
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
                    BlockProducedContext tryProduceBlock = await TryProduceBlock(_blockTree.Head!.Header, CancellationToken.None);
                    if (tryProduceBlock.ProducedBlock is not null)
                    {
                        _blockTree.SuggestBlock(tryProduceBlock.ProducedBlock);
                        return true;
                    }

                    return false;
                }
            }
        }
    }
}
