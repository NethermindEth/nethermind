// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Config.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ConfigProvider_FindIncorrectSettings_Tests
{
    [Test]
    public void CorrectSettingNames_CaseInsensitive()
    {
        JsonConfigSource? jsonSource = new("SampleJson/CorrectSettingNames.json");

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
        (_, IList<(IConfigSource Source, string Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors.Count, Is.EqualTo(0));
    }

    [Test]
    public void NoCategorySettings()
    {
        IEnvironment? env = Substitute.For<IEnvironment>();
        env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() {
            { "NETHERMIND_CLI_SWITCH_LOCAL", "http://localhost:80" },
            { "NETHERMIND_CONFIG", "test2.json" },
            { "NETHERMIND_XYZ", "xyz" },    // not existing, should get error
            { "QWER", "qwerty" }    // not Nethermind setting, no error
        });
        EnvConfigSource? envSource = new(env);

        ConfigProvider? configProvider = new();
        configProvider.AddSource(envSource);

        configProvider.Initialize();

        (string ErrorMsg, IList<(IConfigSource Source, string Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors.Count, Is.EqualTo(1));
        Assert.That(Errors[0].Name, Is.EqualTo("XYZ"));
        Assert.That(ErrorMsg, Is.EqualTo($"ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:|Name:XYZ"));

    }

    [Test]
    public void SettingWithTypos()
    {
        JsonConfigSource? jsonSource = new("SampleJson/ConfigWithTypos.json");

        IEnvironment? env = Substitute.For<IEnvironment>();
        env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() {
            { "NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPERCOUNT", "500" }  // incorrect, should be NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPEERCOUNT
        });
        EnvConfigSource? envSource = new(env);

        ConfigProvider? configProvider = new();
        configProvider.AddSource(jsonSource);
        configProvider.AddSource(envSource);

        configProvider.Initialize();

        (string ErrorMsg, IList<(IConfigSource Source, string Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors.Count, Is.EqualTo(3));
        Assert.That(Errors[0].Name, Is.EqualTo("Concurrenc"));
        Assert.That(Errors[1].Category, Is.EqualTo("BlomConfig"));
        Assert.That(Errors[2].Name, Is.EqualTo("MAXCANDIDATEPERCOUNT"));
        Assert.That(ErrorMsg, Is.EqualTo($"ConfigType:JsonConfigFile|Category:DiscoveRyConfig|Name:Concurrenc{Environment.NewLine}ConfigType:JsonConfigFile|Category:BlomConfig|Name:IndexLevelBucketSizes{Environment.NewLine}ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:NETWORKCONFIG|Name:MAXCANDIDATEPERCOUNT"));
    }

    [Test]
    public void IncorrectFormat()
    {
        IEnvironment? env = Substitute.For<IEnvironment>();
        env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() {
            { "NETHERMIND_NETWORKCONFIGMAXCANDIDATEPEERCOUNT", "500" }  // incorrect, should be NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPEERCOUNT
        });
        EnvConfigSource? envSource = new(env);

        ConfigProvider? configProvider = new();
        configProvider.AddSource(envSource);

        configProvider.Initialize();

        (string ErrorMsg, IList<(IConfigSource Source, string Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors.Count, Is.EqualTo(1));
        Assert.That(Errors[0].Name, Is.EqualTo("NETWORKCONFIGMAXCANDIDATEPEERCOUNT"));
        Assert.That(ErrorMsg, Is.EqualTo($"ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:|Name:NETWORKCONFIGMAXCANDIDATEPEERCOUNT"));
    }

}
