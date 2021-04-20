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
using System.Numerics;
using Nethermind.Api;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Generic;
using Nethermind.Core;
using FluentAssertions;

namespace Nethermind.Mev.Test
{
    [TestFixture]
    public class MevPluginTests
    {
        [Test]
        public void Can_create()
        {
            _ = new MevPlugin();
        }
        
        [Test]
        public void Throws_on_null_api_in_init()
        {
            MevPlugin plugin = new();
            Assert.Throws<ArgumentNullException>(() => plugin.Init(null));
        }
        
        [Test]
        public void Can_initialize()
        {
            INethermindApi api = Substitute.For<INethermindApi>();
            api.Config<IMevConfig>().Returns(MevConfig.Default);
            api.LogManager.Returns(LimboLogs.Instance);

            MevPlugin plugin = new();
            plugin.Init(api);
        }
        
        [Test]
        public void Throws_if_not_initialized_before_rpc_registration()
        {
            MevPlugin plugin = new();
            Assert.Throws<InvalidOperationException>(() => plugin.InitRpcModules());
        }
        
        [Test]
        public void Can_register_json_rpc()
        {
            INethermindApi api = Substitute.For<INethermindApi>();
            api.ForRpc.Returns((api, api));
            api.Config<IMevConfig>().Returns(MevConfig.Default);
            api.Config<IJsonRpcConfig>().Returns(JsonRpcConfig.Default);
            api.LogManager.Returns(LimboLogs.Instance);

            MevPlugin plugin = new();
            plugin.Init(api);
            plugin.InitRpcModules();
        }

        private record BundleTestData(ulong block, ulong testTimestamp, int expectedRes, int expectedRemaining, Action action);

        private MevBundleForRpc BundleHelper(List<Transaction> txs, ulong bNum, ulong minT, ulong maxT)
        {
            return new MevBundleForRpc(txs, new BigInteger(bNum), new BigInteger(minT), new BigInteger(maxT));
        }

        [Test]
        public void should_calculate_appropriate_number_of_bundles()
        {
            INethermindApi api = Substitute.For<INethermindApi>();
            api.Config<IMevConfig>().Returns(MevConfig.Default);
            api.LogManager.Returns(LimboLogs.Instance);

            MevPlugin plugin = new();
            plugin.Init(api);

            var empty = new List<Transaction>();

            plugin.AddMevBundle(BundleHelper(empty, 4, 0, 0));
            plugin.AddMevBundle(BundleHelper(empty, 5, 0, 0));
            plugin.AddMevBundle(BundleHelper(empty, 6, 0, 0));
            plugin.AddMevBundle(BundleHelper(empty, 9, 0, 0));
            plugin.AddMevBundle(BundleHelper(empty, 9, 0, 0));
            plugin.AddMevBundle(BundleHelper(empty, 12, 0, 0));
            plugin.AddMevBundle(BundleHelper(empty, 15, 0, 0));

            var testBundles = new BundleTestData[] 
            {
                new BundleTestData(8, 0, 0, 4, null),
                new BundleTestData(9, 0, 2, 4, null),
                new BundleTestData(10, 8, 0, 2, () => plugin.AddMevBundle(BundleHelper(empty, 10, 5, 7))),
                new BundleTestData(11, 0, 0, 2, null),
                new BundleTestData(12, 0, 1, 2, null),
                new BundleTestData(13, 0, 0, 1, null),
                new BundleTestData(14, 0, 0, 1, null),
                new BundleTestData(15, 0, 1, 1, null),
                new BundleTestData(16, 0, 0, 0, null),
            };

            foreach(var testBundle in testBundles) 
            {
                if(testBundle.action != null) testBundle.action();
                
                var res = plugin.GetCurrentMevTxBundles(testBundle.block, testBundle.testTimestamp);
                res.Count.Should().Be(testBundle.expectedRes);
                plugin.MevBundles.Count.Should().Be(testBundle.expectedRemaining);
            }
        }

    }
}
