// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Config.Test;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Parallelizable(ParallelScope.All)]
public class ConfigProvider_FindIncorrectSettings_Tests
{
    private IEnvironment _env;

    [SetUp]
    public void Initialize()
    {
        _env = Substitute.For<IEnvironment>();
        _env.GetEnvironmentVariable(Arg.Any<string>())
            .Returns(call =>
            {
                IDictionary vars = _env.GetEnvironmentVariables();
                var key = call.Arg<string>();

                return vars.Contains(key) ? vars[key] : null;
            });
    }

    [Test]
    public void CorrectSettingNames_CaseInsensitive()
    {
        JsonConfigSource? jsonSource = new("SampleJson/CorrectSettingNames.json");

        Dictionary<string, string> envVars = new() { { "NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPEERCOUNT", "500" } };

        _env.GetEnvironmentVariables().Returns(envVars);
        EnvConfigSource? envSource = new(_env);

        ArgsConfigSource? argsSource = new(new Dictionary<string, string>() {
            { "DiscoveryConfig.BucketSize", "10" },
            { "NetworkConfig.DiscoveryPort", "30301" } });

        ConfigProvider? configProvider = new();
        configProvider.AddSource(jsonSource);
        configProvider.AddSource(envSource);
        configProvider.AddSource(argsSource);

        configProvider.Initialize();
        (_, IList<(IConfigSource Source, string Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors, Is.Empty);
    }

    [Test]
    public void NoCategorySettings()
    {
        _env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() {
            { "NETHERMIND_CLI_SWITCH_LOCAL", "http://localhost:80" },
            { "NETHERMIND_CONFIG", "test2.json" },
            { "NETHERMIND_XYZ", "xyz" },    // not existing, should get error
            { "QWER", "qwerty" }    // not Nethermind setting, no error
        });
        EnvConfigSource? envSource = new(_env);

        ConfigProvider? configProvider = new();
        configProvider.AddSource(envSource);

        configProvider.Initialize();

        (string ErrorMsg, IList<(IConfigSource Source, string Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(Errors[0].Name, Is.EqualTo("XYZ"));
            Assert.That(ErrorMsg, Is.EqualTo($"ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:|Name:XYZ"));
        });
    }

    [Test]
    public void SettingWithTypos()
    {
        JsonConfigSource? jsonSource = new("SampleJson/ConfigWithTypos.json");

        _env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() {
            { "NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPERCOUNT", "500" }  // incorrect, should be NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPEERCOUNT
        });
        EnvConfigSource? envSource = new(_env);

        ConfigProvider? configProvider = new();
        configProvider.AddSource(jsonSource);
        configProvider.AddSource(envSource);

        configProvider.Initialize();

        (string ErrorMsg, IList<(IConfigSource Source, string Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(Errors[0].Name, Is.EqualTo("Concurrenc"));
            Assert.That(Errors[1].Category, Is.EqualTo("BlomConfig"));
            Assert.That(Errors[2].Name, Is.EqualTo("MAXCANDIDATEPERCOUNT"));
            Assert.That(ErrorMsg, Is.EqualTo($"ConfigType:JsonConfigFile|Category:DiscoveRyConfig|Name:Concurrenc{Environment.NewLine}ConfigType:JsonConfigFile|Category:BlomConfig|Name:IndexLevelBucketSizes{Environment.NewLine}ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:NETWORKCONFIG|Name:MAXCANDIDATEPERCOUNT"));
        });
    }

    [Test]
    public void IncorrectFormat()
    {
        _env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() {
            { "NETHERMIND_NETWORKCONFIGMAXCANDIDATEPEERCOUNT", "500" }  // incorrect, should be NETHERMIND_NETWORKCONFIG_MAXCANDIDATEPEERCOUNT
        });
        EnvConfigSource? envSource = new(_env);

        ConfigProvider? configProvider = new();
        configProvider.AddSource(envSource);

        configProvider.Initialize();

        (string ErrorMsg, IList<(IConfigSource Source, string Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(Errors[0].Name, Is.EqualTo("NETWORKCONFIGMAXCANDIDATEPEERCOUNT"));
            Assert.That(ErrorMsg, Is.EqualTo($"ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:|Name:NETWORKCONFIGMAXCANDIDATEPEERCOUNT"));
        });
    }

    [Test]
    public void Should_keep_blank_string_values()
    {
        Dictionary<string, string> envVars = new()
        {
            { "NETHERMIND_BLOCKSCONFIG_EXTRADATA", "" }
        };

        _env.GetEnvironmentVariables().Returns(envVars);
        EnvConfigSource? envSource = new(_env);

        ConfigProvider? configProvider = new();
        configProvider.AddSource(envSource);

        (bool isSet, object value) = envSource.GetValue(typeof(string), "BlocksConfig", "ExtraData");

        Assert.Multiple(() =>
        {
            Assert.That(isSet, Is.True);
            Assert.That(value, Is.Empty);
        });
    }

    [Test]
    public void Should_ignore_blank_nonstring_values()
    {
        Dictionary<string, string> envVars = new()
        {
            { "NETHERMIND_BLOOMCONFIG_INDEX", " " },
            { "NETHERMIND_BLOOMCONFIG_MIGRATION", "" }
        };

        _env.GetEnvironmentVariables().Returns(envVars);
        EnvConfigSource? envSource = new(_env);

        ConfigProvider? configProvider = new();
        configProvider.AddSource(envSource);

        Assert.DoesNotThrow(configProvider.Initialize);

        (bool isSet, object value) = envSource.GetValue(typeof(bool), "BloomConfig", "Index");

        Assert.Multiple(() =>
        {
            Assert.That(isSet, Is.False);
            Assert.That(((ValueTuple<bool, object>)value).Item2, Is.False);
        });

        (isSet, value) = envSource.GetValue(typeof(bool), "BloomConfig", "Migration");

        Assert.Multiple(() =>
        {
            Assert.That(isSet, Is.False);
            Assert.That(((ValueTuple<bool, object>)value).Item2, Is.False);
        });
    }
}
