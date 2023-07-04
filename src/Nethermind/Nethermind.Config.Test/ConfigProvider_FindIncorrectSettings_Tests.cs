// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            JsonConfigSource? jsonSource = new("SampleJson/CorrectSettingNames.cfg");

            IEnvironment? env = Substitute.For<IEnvironment>();
            env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() { { "NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPEERCOUNT", "500" } });
            EnvConfigSource? envSource = new(env);

            ArgsConfigSource? argsSource = new(new Dictionary<string, string>() {
                { "DiscoveryConfig.BucketSize", "10" },
                { "NetworkConfig.DiscoveryPort", "30301" } });

            ConfigProvider? configProvider = new();
            configProvider.AddSource(jsonSource);
            configProvider.AddSource(envSource);
            configProvider.AddSource(argsSource);

            configProvider.Initialize();


            (string ErrorMsg, IList<(IConfigSource Source, string Category, string Name)> Errors) res = configProvider.FindIncorrectSettings();

            Assert.That(res.Errors.Count, Is.EqualTo(0));
        }

        [Test]
        public void NoCategorySettings()
        {
            IEnvironment? env = Substitute.For<IEnvironment>();
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
            EnvConfigSource? envSource = new(env);

            ArgsConfigSource? argsSource = new(new Dictionary<string, string>() {
                { "config", "test.cfg" },
                { "datadir", "Data" },
                { "ConfigsDirectory", "ConfDir" },
                { "baseDbPath", "DB" },
                { "log", "info" },
                { "loggerConfigSource", "logSource" },
                { "pluginsDirectory", "Plugins" },
                { "Abc", "abc" }    // not existing, should get error
            });

            ConfigProvider? configProvider = new();
            configProvider.AddSource(envSource);
            configProvider.AddSource(argsSource);

            configProvider.Initialize();

            (string ErrorMsg, IList<(IConfigSource Source, string Category, string Name)> Errors) res = configProvider.FindIncorrectSettings();

            Assert.That(res.Errors.Count, Is.EqualTo(2));
            Assert.That(res.Errors[0].Name, Is.EqualTo("XYZ"));
            Assert.That(res.Errors[1].Name, Is.EqualTo("Abc"));
            Assert.That(res.ErrorMsg, Is.EqualTo($"ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:|Name:XYZ{Environment.NewLine}ConfigType:RuntimeOption|Category:|Name:Abc"));

        }

        [Test]
        public void SettingWithTypos()
        {
            JsonConfigSource? jsonSource = new("SampleJson/ConfigWithTypos.cfg");

            IEnvironment? env = Substitute.For<IEnvironment>();
            env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() {
                { "NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPERCOUNT", "500" }  // incorrect, should be NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPEERCOUNT
            });
            EnvConfigSource? envSource = new(env);

            ArgsConfigSource? argsSource = new(new Dictionary<string, string>() {
                { "DiscoveryConfig.BucketSize", "10" },
                { "NetworkConfig.DiscoverPort", "30301" }, // incorrect, should be NetworkConfig.DiscoveryPort
                { "Network.P2PPort", "30301" } });

            ConfigProvider? configProvider = new();
            configProvider.AddSource(jsonSource);
            configProvider.AddSource(envSource);
            configProvider.AddSource(argsSource);

            configProvider.Initialize();

            (string ErrorMsg, IList<(IConfigSource Source, string Category, string Name)> Errors) res = configProvider.FindIncorrectSettings();

            Assert.That(res.Errors.Count, Is.EqualTo(4));
            Assert.That(res.Errors[0].Name, Is.EqualTo("Concurrenc"));
            Assert.That(res.Errors[1].Category, Is.EqualTo("BlomConfig"));
            Assert.That(res.Errors[2].Name, Is.EqualTo("MAXCANDIDATEPERCOUNT"));
            Assert.That(res.Errors[3].Name, Is.EqualTo("DiscoverPort"));
            Assert.That(res.ErrorMsg, Is.EqualTo($"ConfigType:JsonConfigFile|Category:DiscoveRyConfig|Name:Concurrenc{Environment.NewLine}ConfigType:JsonConfigFile|Category:BlomConfig|Name:IndexLevelBucketSizes{Environment.NewLine}ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:NETWORKCONFIG|Name:MAXCANDIDATEPERCOUNT{Environment.NewLine}ConfigType:RuntimeOption|Category:NetworkConfig|Name:DiscoverPort"));
        }

        [Test]
        public void IncorrectFormat()
        {
            IEnvironment? env = Substitute.For<IEnvironment>();
            env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() {
                { "NETHERMIND_NETWORKCONFIGMAXCANDIDATEPEERCOUNT", "500" }  // incorrect, should be NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPEERCOUNT
            });
            EnvConfigSource? envSource = new(env);

            ArgsConfigSource? argsSource = new(new Dictionary<string, string>() {
                { "DiscoveryConfig.BucketSize", "10" },
                { "NetworkConfigP2PPort", "30301" } }); // incorrect, should be Network.P2PPort

            ConfigProvider? configProvider = new();
            configProvider.AddSource(envSource);
            configProvider.AddSource(argsSource);

            configProvider.Initialize();

            (string ErrorMsg, IList<(IConfigSource Source, string Category, string Name)> Errors) res = configProvider.FindIncorrectSettings();

            Assert.That(res.Errors.Count, Is.EqualTo(2));
            Assert.That(res.Errors[0].Name, Is.EqualTo("NETWORKCONFIGMAXCANDIDATEPEERCOUNT"));
            Assert.That(res.Errors[1].Name, Is.EqualTo("NetworkConfigP2PPort"));
            Assert.That(res.ErrorMsg, Is.EqualTo($"ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:|Name:NETWORKCONFIGMAXCANDIDATEPEERCOUNT{Environment.NewLine}ConfigType:RuntimeOption|Category:|Name:NetworkConfigP2PPort"));
        }

    }
}
