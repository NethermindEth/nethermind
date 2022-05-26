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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;
using NSubstitute;
using Build = Nethermind.Runner.Test.Ethereum.Build;

namespace Nethermind.Merge.Plugin.Test
{
    public class MergePluginTests
    {
        private MergeConfig _mergeConfig = null!;
        private NethermindApi _context = null!;
        private MergePlugin _plugin = null!;
        private CliquePlugin _consensusPlugin = null;

        [SetUp]
        public void Setup()
        {
            _mergeConfig = new MergeConfig() {Enabled = true, FeeRecipient = TestItem.AddressA.ToString()};
            MiningConfig? miningConfig = new() {Enabled = true};
            _context = Build.ContextWithMocks();
            _context.SealEngineType = SealEngineType.Clique;
            _context.ConfigProvider.GetConfig<IMergeConfig>().Returns(_mergeConfig);
            _context.ConfigProvider.GetConfig<ISyncConfig>().Returns(new SyncConfig());
            _context.ConfigProvider.GetConfig<IMiningConfig>().Returns(miningConfig);
            _context.BlockProcessingQueue?.IsEmpty.Returns(true);
            _context.MemDbFactory = new MemDbFactory();
            _context.BlockProducerEnvFactory = new BlockProducerEnvFactory(
                _context.DbProvider!,
                _context.BlockTree!,
                _context.ReadOnlyTrieStore!,
                _context.SpecProvider!,
                _context.BlockValidator!,
                _context.RewardCalculatorSource!,
                _context.ReceiptStorage!,
                _context.BlockPreprocessor!,
                _context.TxPool!,
                _context.TransactionComparerProvider,
                miningConfig,
                _context.LogManager!);
            _context.ChainSpec!.Clique = new CliqueParameters()
            {
                Epoch = CliqueConfig.Default.Epoch,
                Period = CliqueConfig.Default.BlockPeriod
            };
            _plugin = new MergePlugin();
            
            _consensusPlugin = new();
        }
        
        [TestCase(true)]
        [TestCase(false)]
        public void Init_merge_plugin_does_not_throw_exception(bool enabled)
        {
            _mergeConfig.Enabled = enabled;
            Assert.DoesNotThrowAsync(async () => await _consensusPlugin.Init(_context));
            Assert.DoesNotThrowAsync(async () => await _plugin.Init(_context));
            Assert.DoesNotThrowAsync(async () => await _plugin.InitNetworkProtocol());
            Assert.DoesNotThrowAsync(async () => await _plugin.InitSynchronization());
            Assert.DoesNotThrowAsync(async () => await _plugin.InitBlockProducer(_consensusPlugin));
            Assert.DoesNotThrowAsync(async () => await _plugin.InitRpcModules());
            Assert.DoesNotThrowAsync(async () => await _plugin.DisposeAsync());
        }
        
        [Test]
        public async Task Initializes_correctly()
        {
            Assert.DoesNotThrowAsync(async () => await _consensusPlugin.Init(_context));
            await _plugin.Init(_context);
            await _plugin.InitSynchronization();
            await _plugin.InitNetworkProtocol();
            ISyncConfig syncConfig = _context.Config<ISyncConfig>();
            Assert.IsTrue(syncConfig.NetworkingEnabled);
            Assert.IsTrue(_context.GossipPolicy.CanGossipBlocks);
            await _plugin.InitBlockProducer(_consensusPlugin);
            Assert.IsInstanceOf<MergeBlockProducer>(_context.BlockProducer);
            await _plugin.InitRpcModules();
            _context.RpcModuleProvider.Received().Register(Arg.Is<IRpcModulePool<IEngineRpcModule>>(m => m is SingletonModulePool<IEngineRpcModule>));
            await _plugin.DisposeAsync();
        }
    }
}
