/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.IO;
using System.Net;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Runner.Config;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    public class ConfigFilesTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg")]
        [TestCase("mainnet.cfg")]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg")]
        public void Required_config_files_exist(string configFile)
        {
            var configPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "configs", configFile);
            Assert.True(File.Exists(configPath));
        }
        
        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg", true)]
        [TestCase("mainnet.cfg", true)]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg", true)]
        public void Basic_config_are_as_expected(string configFile, bool isProduction = false)
        {
            ConfigProvider configProvider = new ConfigProvider();
            var configPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "configs", configFile);
            configProvider.AddSource(new JsonConfigSource(configPath));

            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            Assert.True(initConfig.SynchronizationEnabled, nameof(initConfig.SynchronizationEnabled));
            Assert.True(initConfig.ProcessingEnabled, nameof(initConfig.ProcessingEnabled));
            Assert.False(initConfig.PubSubEnabled, nameof(initConfig.PubSubEnabled));
            Assert.True(initConfig.DiscoveryEnabled, nameof(initConfig.DiscoveryEnabled));
            Assert.True(initConfig.PeerManagerEnabled, nameof(initConfig.PeerManagerEnabled));
            Assert.False(initConfig.WebSocketsEnabled, nameof(initConfig.WebSocketsEnabled));
            if (isProduction)
            {
                Assert.False(initConfig.EnableUnsecuredDevWallet, nameof(initConfig.EnableUnsecuredDevWallet));
            }

            Assert.False(initConfig.KeepDevWalletInMemory, nameof(initConfig.KeepDevWalletInMemory));
            Assert.False(initConfig.JsonRpcEnabled, nameof(initConfig.JsonRpcEnabled));
            Assert.False(initConfig.IsMining, nameof(initConfig.IsMining));
            Assert.True(initConfig.StoreReceipts, nameof(initConfig.StoreReceipts));
            Assert.False(initConfig.EnableRc7Fix, nameof(initConfig.EnableRc7Fix));
            Assert.AreEqual(8545, initConfig.HttpPort, nameof(initConfig.HttpPort));
            Assert.AreEqual(30303, initConfig.DiscoveryPort, nameof(initConfig.DiscoveryPort));
            Assert.AreEqual(30303, initConfig.P2PPort, nameof(initConfig.P2PPort));
            Assert.AreEqual(configFile.Replace("cfg", "logs.txt"), initConfig.LogFileName, nameof(initConfig.LogFileName));
            Assert.False(initConfig.StoreTraces, nameof(initConfig.StoreTraces));
            Assert.AreEqual("chainspec", initConfig.ChainSpecFormat, nameof(initConfig.ChainSpecFormat));
            Assert.False(initConfig.StoreTraces, nameof(initConfig.StoreTraces));
        }
    }
}