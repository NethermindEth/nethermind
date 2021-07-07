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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.KeyStore.Config;
using Nethermind.Network.Config;
using Nethermind.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class ConfigProvider_FindIncorrectSettings_Tests
    { 
        [Test]
        public void CorrectSettingNames_CaseInsensitive()
        {
            var jsonSource = new JsonConfigSource("SampleJson/CorrectSettingNames.cfg");

            var env = Substitute.For<IEnvironment>();
            env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() { { "NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPEERCOUNT", "500" } });
            var envSource = new EnvConfigSource(env);

            var argsSource = new ArgsConfigSource(new Dictionary<string, string>() { { "DiscoveryConfig.BucketSize", "10" }, { "NetworkConfig.DiscoveryPort", "30301" } });

            var configProvider = new ConfigProvider();
            configProvider.AddSource(jsonSource);
            configProvider.AddSource(envSource);
            configProvider.AddSource(argsSource);

            configProvider.Initialize();


            var res = configProvider.FindIncorrectSettings();

            Assert.AreEqual(0, res.Errors.Count);
        }

        [Test]
        public void SettingWithTypos()
        {
            var jsonSource = new JsonConfigSource("SampleJson/ConfigWithTypos.cfg");

            var env = Substitute.For<IEnvironment>();
            env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() { { "NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPERCOUNT", "500" } });
            var envSource = new EnvConfigSource(env);

            var argsSource = new ArgsConfigSource(new Dictionary<string, string>() { { "DiscoveryConfig.BucketSize", "10" }, { "NetworkConfig.DiscoverPort", "30301" }, { "Network.P2PPort", "30301" } });

            var configProvider = new ConfigProvider();
            configProvider.AddSource(jsonSource);
            configProvider.AddSource(envSource);
            configProvider.AddSource(argsSource);

            configProvider.Initialize();

            var res = configProvider.FindIncorrectSettings();

            Assert.AreEqual(5, res.Errors.Count);
            Assert.AreEqual("Concurrenc", res.Errors[0].Name);
            Assert.AreEqual("BlomConfig", res.Errors[1].Category);
            Assert.AreEqual("MAXCANDIDATEPERCOUNT", res.Errors[2].Name);
            Assert.AreEqual("DiscoverPort", res.Errors[3].Name);
            Assert.AreEqual("Network", res.Errors[4].Category);
            Assert.AreEqual($"Nethermind.Config.JsonConfigSource:DiscoveRyConfig:Concurrenc{Environment.NewLine}Nethermind.Config.JsonConfigSource:BlomConfig:IndexLevelBucketSizes{Environment.NewLine}Nethermind.Config.EnvConfigSource:NETWORKCONFIG:MAXCANDIDATEPERCOUNT{Environment.NewLine}Nethermind.Config.ArgsConfigSource:NetworkConfig:DiscoverPort{Environment.NewLine}Nethermind.Config.ArgsConfigSource:Network:P2PPort", res.ErrorMsg);
        }

        [Test]
        public void IncorrectFormat()
        {
            var env = Substitute.For<IEnvironment>();
            env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() { { "NETHERMIND_NETWORKCONFIGMAXCANDIDATEPEERCOUNT", "500" } });
            var envSource = new EnvConfigSource(env);

            var argsSource = new ArgsConfigSource(new Dictionary<string, string>() { { "DiscoveryConfig.BucketSize", "10" }, { "NetworkConfigP2PPort", "30301" } });

            var configProvider = new ConfigProvider();
            configProvider.AddSource(envSource);
            configProvider.AddSource(argsSource);

            configProvider.Initialize();

            var res = configProvider.FindIncorrectSettings();

            Assert.AreEqual(2, res.Errors.Count);
            Assert.AreEqual("NETWORKCONFIGMAXCANDIDATEPEERCOUNT", res.Errors[0].Category);
            Assert.AreEqual("NetworkConfigP2PPort", res.Errors[1].Category);
            Assert.AreEqual($"Nethermind.Config.EnvConfigSource:NETWORKCONFIGMAXCANDIDATEPEERCOUNT:{Environment.NewLine}Nethermind.Config.ArgsConfigSource:NetworkConfigP2PPort:", res.ErrorMsg);
        }

    }
}
