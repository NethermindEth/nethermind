// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
                string key = call.Arg<string>();

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
        (_, IList<(IConfigSource Source, string? Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors, Is.Empty);
    }

    [Test]
    public void NoCategorySettings()
    {
        _env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() {
            { "NETHERMIND_CLI_SWITCH_LOCAL", "http://localhost:80" },
            { "NETHERMIND_CONFIG", "test2.json" },
            { "NETHERMIND_XYZ", "xyz" },    // not existing, should get error
            { "QWERTY", "qwerty" }    // not Nethermind setting, no error
        });
        EnvConfigSource? envSource = new(_env);

        ConfigProvider? configProvider = new();
        configProvider.AddSource(envSource);

        configProvider.Initialize();

        (string ErrorMsg, IList<(IConfigSource Source, string? Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors.Count, Is.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors[0].Name, Is.EqualTo("XYZ"));
            Assert.That(ErrorMsg, Is.EqualTo($"ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:|Name:XYZ"));
        }
    }

    [Test]
    public void Unread_settings_without_a_category_are_reported_as_deprecated()
    {
        _env.GetEnvironmentVariables().Returns(new Dictionary<string, string>() {
            { "NETHERMIND_CLISWITCHLOCAL", "http://localhost:80" },
            { "NETHERMIND_MONITORINGJOB", "job" },
            { "NETHERMIND_MONITORINGGROUP", "group" }
        });
        ConfigProvider configProvider = new();
        configProvider.AddSource(new EnvConfigSource(_env));

        Assert.That(configProvider.GetObsoleteSettingWarnings(), Is.EquivalentTo(new[]
        {
            "CliSwitchLocal is deprecated: Has no effect.",
            "MonitoringJob is deprecated: Has no effect. Set Metrics.MonitoringJob.",
            "MonitoringGroup is deprecated: Has no effect. Set Metrics.MonitoringGroup."
        }));
    }

    // The members are named by strings, so the test itself does not use them.
    [TestCase(typeof(ConfigItemAttribute), "EnvironmentVariable")]
    [TestCase(typeof(INoCategoryConfig), "CliSwitchLocal")]
    [TestCase(typeof(NoCategoryConfig), "CliSwitchLocal")]
    [TestCase(typeof(NoCategoryConfig), "MonitoringJob")]
    [TestCase(typeof(NoCategoryConfig), "MonitoringGroup")]
    public void Unread_members_are_deprecated_but_kept(Type type, string member) =>
        Assert.That(type.GetProperty(member)?.GetCustomAttribute<ObsoleteAttribute>(), Is.Not.Null.And.Property(nameof(ObsoleteAttribute.IsError)).False);

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

        (string ErrorMsg, IList<(IConfigSource Source, string? Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors.Count, Is.EqualTo(3));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors[0].Name, Is.EqualTo("Concurrenc"));
            Assert.That(Errors[1].Category, Is.EqualTo("BloomsConfig"));
            Assert.That(Errors[2].Name, Is.EqualTo("MAXCANDIDATEPERCOUNT"));
            Assert.That(ErrorMsg, Is.EqualTo($"ConfigType:JsonConfigFile|Category:DiscoveRyConfig|Name:Concurrenc{Environment.NewLine}ConfigType:JsonConfigFile|Category:BloomsConfig|Name:IndexLevelBucketSizes{Environment.NewLine}ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:NETWORKCONFIG|Name:MAXCANDIDATEPERCOUNT"));
        }
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

        (string ErrorMsg, IList<(IConfigSource Source, string? Category, string Name)> Errors) = configProvider.FindIncorrectSettings();

        Assert.That(Errors.Count, Is.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors[0].Name, Is.EqualTo("NETWORKCONFIGMAXCANDIDATEPEERCOUNT"));
            Assert.That(ErrorMsg, Is.EqualTo($"ConfigType:EnvironmentVariable(NETHERMIND_*)|Category:|Name:NETWORKCONFIGMAXCANDIDATEPEERCOUNT"));
        }
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

        (bool isSet, object? value) = envSource.GetValue(typeof(string), "BlocksConfig", "ExtraData");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(isSet, Is.True);
            Assert.That(value, Is.Empty);
        }
    }

    [Test]
    public void Should_ignore_blank_nonstring_values()
    {
        Dictionary<string, string> envVars = new()
        {
            { "NETHERMIND_SYNCCONFIG_FASTSYNC", " " },
            { "NETHERMIND_SYNCCONFIG_SNAPSYNC", "" }
        };

        _env.GetEnvironmentVariables().Returns(envVars);
        EnvConfigSource? envSource = new(_env);

        ConfigProvider? configProvider = new();
        configProvider.AddSource(envSource);

        Assert.DoesNotThrow(configProvider.Initialize);

        (bool isSet, object? value) = envSource.GetValue(typeof(bool), "SyncConfig", "FastSync");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(isSet, Is.False);
            Assert.That(value, Is.False);
        }

        (isSet, value) = envSource.GetValue(typeof(bool), "SyncConfig", "SnapSync");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(isSet, Is.False);
            Assert.That(value, Is.False);
        }
    }
}
