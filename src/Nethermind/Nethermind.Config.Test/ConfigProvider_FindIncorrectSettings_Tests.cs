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
using System.Collections.Generic;
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

            var argsSource = new ArgsConfigSource(new Dictionary<string, string>() { 
                { "DiscoveryConfig.BucketSize", "10" }, 
                { "NetworkConfig.DiscoveryPort", "30301" } });

            var configProvider = new ConfigProvider();
            configProvider.AddSource(jsonSource);
            configProvider.AddSource(envSource);
            configProvider.AddSource(argsSource);

            configProvider.Initialize();


            var res = configProvider.FindIncorrectSettings();

            Assert.AreEqual(0, res.Errors.Count);
        }

        [Test]
        public void NoCategorySettings()
        {
            var env = Substitute.For<IEnvironment>();
            env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() { 
                { "NETHERMIND_CLI_SWITCH_LOCAL", "http://localhost:80" },
                { "NETHERMIND_MONITORING_JOB", "nethermindJob" },
                { "NETHERMIND_MONITORING_GROUP", "nethermindGroup" },
                { "NETHERMIND_ENODE_IPADDRESS", "1.2.3.4" },
                { "NETHERMIND_HIVE_ENABLED", "true" },
                { "NETHERMIND_URL", "http://test:80" },
                { "NETHERMIND_CORS_ORIGINS", "*" },
                { "NETHERMIND_CONFIG", "test2.cfg" },
                { "NETHERMIND_XYZ", "xyz" },    // not existing, should get error
                { "QWER", "qwerty" }    // not Nethermind setting, no error
            }); 
            var envSource = new EnvConfigSource(env);

            var argsSource = new ArgsConfigSource(new Dictionary<string, string>() {
                { "config", "test.cfg" },
                { "datadir", "Data" },
                { "ConfigsDirectory", "ConfDir" },
                { "baseDbPath", "DB" },
                { "logLevel", "info" },
                { "loggerConfigSource", "logSource" },
                { "pluginsDirectory", "Plugins" },
                { "Abc", "abc" }    // not existing, should get error
            });

            var configProvider = new ConfigProvider();
            configProvider.AddSource(envSource);
            configProvider.AddSource(argsSource);

            configProvider.Initialize();

            var res = configProvider.FindIncorrectSettings();

            Assert.AreEqual(2, res.Errors.Count);
            Assert.AreEqual("XYZ", res.Errors[0].Name);
            Assert.AreEqual("Abc", res.Errors[1].Name);
            Assert.AreEqual($"ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:|Name:XYZ{Environment.NewLine}ConfigType:RuntimeOption|Category:|Name:Abc", res.ErrorMsg);

        }

        [Test]
        public void SettingWithTypos()
        {
            var jsonSource = new JsonConfigSource("SampleJson/ConfigWithTypos.cfg");

            var env = Substitute.For<IEnvironment>();
            env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() { 
                { "NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPERCOUNT", "500" }  // incorrect, should be NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPEERCOUNT
            });
            var envSource = new EnvConfigSource(env);

            var argsSource = new ArgsConfigSource(new Dictionary<string, string>() { 
                { "DiscoveryConfig.BucketSize", "10" }, 
                { "NetworkConfig.DiscoverPort", "30301" }, // incorrect, should be NetworkConfig.DiscoveryPort
                { "Network.P2PPort", "30301" } });

            var configProvider = new ConfigProvider();
            configProvider.AddSource(jsonSource);
            configProvider.AddSource(envSource);
            configProvider.AddSource(argsSource);

            configProvider.Initialize();

            var res = configProvider.FindIncorrectSettings();

            Assert.AreEqual(4, res.Errors.Count);
            Assert.AreEqual("Concurrenc", res.Errors[0].Name);
            Assert.AreEqual("BlomConfig", res.Errors[1].Category);
            Assert.AreEqual("MAXCANDIDATEPERCOUNT", res.Errors[2].Name);
            Assert.AreEqual("DiscoverPort", res.Errors[3].Name);
            Assert.AreEqual($"ConfigType:JsonConfigFile|Category:DiscoveRyConfig|Name:Concurrenc{Environment.NewLine}ConfigType:JsonConfigFile|Category:BlomConfig|Name:IndexLevelBucketSizes{Environment.NewLine}ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:NETWORKCONFIG|Name:MAXCANDIDATEPERCOUNT{Environment.NewLine}ConfigType:RuntimeOption|Category:NetworkConfig|Name:DiscoverPort", res.ErrorMsg);
        }

        [Test]
        public void IncorrectFormat()
        {
            var env = Substitute.For<IEnvironment>();
            env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() { 
                { "NETHERMIND_NETWORKCONFIGMAXCANDIDATEPEERCOUNT", "500" }  // incorrect, should be NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPEERCOUNT
            });
            var envSource = new EnvConfigSource(env);

            var argsSource = new ArgsConfigSource(new Dictionary<string, string>() { 
                { "DiscoveryConfig.BucketSize", "10" }, 
                { "NetworkConfigP2PPort", "30301" } }); // incorrect, should be Network.P2PPort

            var configProvider = new ConfigProvider();
            configProvider.AddSource(envSource);
            configProvider.AddSource(argsSource);

            configProvider.Initialize();

            var res = configProvider.FindIncorrectSettings();

            Assert.AreEqual(2, res.Errors.Count);
            Assert.AreEqual("NETWORKCONFIGMAXCANDIDATEPEERCOUNT", res.Errors[0].Name);
            Assert.AreEqual("NetworkConfigP2PPort", res.Errors[1].Name);
            Assert.AreEqual($"ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:|Name:NETWORKCONFIGMAXCANDIDATEPEERCOUNT{Environment.NewLine}ConfigType:RuntimeOption|Category:|Name:NetworkConfigP2PPort", res.ErrorMsg);
        }

    }
}
