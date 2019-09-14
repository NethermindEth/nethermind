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

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.KeyStore.Config;
using Nethermind.Network.Config;
using Nethermind.Stats;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    public class DefaultConfigProviderTests
    {
        [SetUp]
        public void Initialize()
        {
            var keystoreConfig = new KeyStoreConfig();
            var networkConfig = new NetworkConfig();
            var jsonRpcConfig = new JsonRpcConfig();
            var statsConfig = new StatsConfig();
        }


        [Test]
        public void Can_read_without_sources()
        {
            ConfigProvider configProvider = new ConfigProvider();
            IStatsConfig statsConfig = configProvider.GetConfig<IStatsConfig>();
            Assert.AreEqual(1000500L, statsConfig.PredefinedReputation);
        }

        [Test]
        public void Can_read_overwrites()
        {
            BitArray bitArray = new BitArray(6);
            for (int i = 0; i < 2 * 2 * 2 * 2 * 2 * 2; i++)
            {
                ConfigProvider configProvider = new ConfigProvider();
                bitArray.Set(0, (i >> 0) % 2 == 1);
                bitArray.Set(1, (i >> 1) % 2 == 1);
                bitArray.Set(2, (i >> 2) % 2 == 1);
                bitArray.Set(3, (i >> 3) % 2 == 1);
                bitArray.Set(4, (i >> 4) % 2 == 1);
                bitArray.Set(5, (i >> 5) % 2 == 1);

                Dictionary<string, string> args = new Dictionary<string, string>();
                if (bitArray.Get(4))
                {
                    args.Add("Stats.CaptureNodeStatsEventHistory", bitArray.Get(5).ToString());
                }

                Environment.SetEnvironmentVariable("NETHERMIND_STATSCONFIG_CAPTURENODESTATSEVENTHISTORY", null, EnvironmentVariableTarget.Process);
                if (bitArray.Get(2))
                {
                    Environment.SetEnvironmentVariable("NETHERMIND_STATSCONFIG_CAPTURENODESTATSEVENTHISTORY", bitArray.Get(3).ToString(), EnvironmentVariableTarget.Process);
                }

                Dictionary<string, string> fakeJson = new Dictionary<string, string>();
                if (bitArray.Get(0))
                {
                    fakeJson.Add("Stats.CaptureNodeStatsEventHistory", bitArray.Get(1).ToString());
                }

                configProvider.AddSource(new ArgsConfigSource(args));
                configProvider.AddSource(new EnvConfigSource());
                configProvider.AddSource(new ArgsConfigSource(fakeJson));

                var config = configProvider.GetConfig<IStatsConfig>();
                bool expectedResult = bitArray.Get(4)
                    ? bitArray.Get(5)
                    : bitArray.Get(2)
                        ? bitArray.Get(3)
                        : bitArray.Get(0)
                            ? bitArray.Get(1)
                            : false;

                Assert.AreEqual(expectedResult, config.CaptureNodeStatsEventHistory, bitArray.ToBitString());
            }
        }
    }
}