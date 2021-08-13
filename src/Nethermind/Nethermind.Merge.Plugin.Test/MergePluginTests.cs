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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Runner.Ethereum.Api;
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

        [SetUp]
        public void Setup()
        {
            _mergeConfig = new MergeConfig() {Enabled = true, BlockAuthorAccount = TestItem.AddressA.ToString()};
            _context = Build.ContextWithMocks();
            _context.ConfigProvider.GetConfig<IMergeConfig>().Returns(_mergeConfig);
            _context.ConfigProvider.GetConfig<ISyncConfig>().Returns(new SyncConfig());
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
                new MiningConfig(),
                _context.LogManager!);
            _plugin = new MergePlugin();
        }
        
        [TestCase(true)]
        [TestCase(false)]
        public void Init_merge_plugin_does_not_throw_exception(bool enabled)
        {
            _mergeConfig.Enabled = enabled;
            Assert.DoesNotThrowAsync(async () => await _plugin.Init(_context));
            Assert.DoesNotThrowAsync(async () => await _plugin.InitNetworkProtocol());
            Assert.DoesNotThrowAsync(async () => await _plugin.InitBlockProducer());
            Assert.DoesNotThrowAsync(async () => await _plugin.InitRpcModules());
            Assert.DoesNotThrowAsync(async () => await _plugin.DisposeAsync());
        }
        
        [Test]
        public async Task Initializes_correctly()
        {
            await _plugin.Init(_context);
            await _plugin.InitNetworkProtocol();
            ISyncConfig syncConfig = _context.Config<ISyncConfig>();
            Assert.IsFalse(syncConfig.SynchronizationEnabled);
            Assert.IsTrue(syncConfig.NetworkingEnabled);
            Assert.IsFalse(syncConfig.BlockGossipEnabled);
            await _plugin.InitBlockProducer();
            Assert.IsInstanceOf<Eth2BlockProducer>(_context.BlockProducer);
            await _plugin.InitRpcModules();
            _context.RpcModuleProvider.Received().Register(Arg.Is<IRpcModulePool<IConsensusRpcModule>>(m => m is SingletonModulePool<IConsensusRpcModule>));
            await _context.BlockchainProcessor!.Received().StopAsync(true);
            await _plugin.DisposeAsync();
        }
    }
}
