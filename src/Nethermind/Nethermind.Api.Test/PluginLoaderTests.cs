// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.HealthChecks;
using Nethermind.Hive;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Api.Test;

public class PluginLoaderTests
{
    [Test]
    public void full_lexicographical_order()
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IPluginLoader loader = new PluginLoader(string.Empty, fileSystem, new TestLogManager().GetClassLogger(),
            typeof(AuRaPlugin),
            typeof(CliquePlugin),
            typeof(EthashPlugin),
            typeof(NethDevPlugin),
            typeof(HivePlugin),
            typeof(TestPlugin));
        loader.Load();
        loader.OrderPlugins(new PluginConfig { PluginOrder = [] });
        var expected = new List<Type>
        {
            typeof(AuRaPlugin),
            typeof(CliquePlugin),
            typeof(EthashPlugin),
            typeof(NethDevPlugin),
            typeof(HivePlugin),
            typeof(TestPlugin)
        };
        Assert.That(expected, Is.EqualTo(loader.PluginTypes).AsCollection);
    }

    [Test]
    public void full_order()
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IPluginLoader loader = new PluginLoader(string.Empty, fileSystem, new TestLogManager().GetClassLogger(),
            typeof(AuRaPlugin),
            typeof(CliquePlugin),
            typeof(EthashPlugin),
            typeof(NethDevPlugin),
            typeof(HivePlugin),
            typeof(TestPlugin));
        loader.Load();
        IPluginConfig pluginConfig =
            new PluginConfig { PluginOrder = ["Hive", "TestPlugin", "NethDev", "Ethash", "Clique", "Aura"] };
        loader.OrderPlugins(pluginConfig);

        var expected = new List<Type>
        {
            typeof(NethDevPlugin),
            typeof(EthashPlugin),
            typeof(CliquePlugin),
            typeof(AuRaPlugin),
            typeof(HivePlugin),
            typeof(TestPlugin)
        };
        Assert.That(expected, Is.EqualTo(loader.PluginTypes).AsCollection);
    }

    [Test]
    public void throws_when_multiple_consensus_plugin()
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        PluginLoader loader = new PluginLoader(
            string.Empty,
            fileSystem,
            new TestLogManager().GetClassLogger(),
            typeof(AuRaPlugin),
            typeof(AnotherAura),
            typeof(CliquePlugin),
            typeof(EthashPlugin),
            typeof(NethDevPlugin),
            typeof(HivePlugin),
            typeof(TestPlugin));
        loader.Load();
        loader.OrderPlugins(new PluginConfig { PluginOrder = [] });

        IConfigProvider configProvider = new ConfigProvider();
        ChainSpec chainSpec = new ChainSpec();
        chainSpec.SealEngineType = SealEngineType.AuRa;

        loader.LoadPlugins(configProvider, chainSpec).Should().Throws<InvalidOperationException>();
    }

    [Test]
    public void partial_lexicographical_order()
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IPluginLoader loader = new PluginLoader(string.Empty, fileSystem, new TestLogManager().GetClassLogger(),
            typeof(AuRaPlugin), typeof(CliquePlugin), typeof(EthashPlugin), typeof(NethDevPlugin), typeof(HivePlugin), typeof(TestPlugin));
        loader.Load();
        IPluginConfig pluginConfig =
            new PluginConfig() { PluginOrder = ["Hive", "NethDev", "Ethash"] };
        loader.OrderPlugins(pluginConfig);

        var expected = new List<Type>
        {
            typeof(NethDevPlugin),
            typeof(EthashPlugin),
            typeof(AuRaPlugin),
            typeof(CliquePlugin),
            typeof(HivePlugin),
            typeof(TestPlugin)
        };
        Assert.That(expected, Is.EqualTo(loader.PluginTypes).AsCollection);
    }

    [Test]
    public void default_config()
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IPluginLoader loader = new PluginLoader(string.Empty, fileSystem, new TestLogManager().GetClassLogger(),
            typeof(EthashPlugin), typeof(NethDevPlugin), typeof(HivePlugin), typeof(HealthChecksPlugin), typeof(MergePlugin));
        loader.Load();
        IPluginConfig pluginConfig =
            new PluginConfig();
        loader.OrderPlugins(pluginConfig);

        var expected = new List<Type>
        {
            typeof(EthashPlugin),
            typeof(NethDevPlugin),
            typeof(MergePlugin),
            typeof(HealthChecksPlugin),
            typeof(HivePlugin)
        };
        Assert.That(expected, Is.EqualTo(loader.PluginTypes).AsCollection);
    }

    [Test]
    public async Task Can_PassInConfig_And_OnlyLoadEnabledPlugins()
    {
        PluginLoader loader = new PluginLoader(string.Empty, Substitute.For<IFileSystem>(), new TestLogManager().GetClassLogger(),
            typeof(TestPlugin1), typeof(TestPlugin2));
        loader.Load();

        IConfigProvider configProvider = new ConfigProvider();
        IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
        initConfig.DiscoveryEnabled = true;
        initConfig.PeerManagerEnabled = false;
        ChainSpec chainSpec = new ChainSpec();
        chainSpec.ChainId = 999;

        IList<INethermindPlugin> loadedPlugins = await loader.LoadPlugins(configProvider, chainSpec);
        loadedPlugins.Should().BeEquivalentTo([new TestPlugin1(chainSpec, initConfig)]);
    }

    private class TestPlugin1(ChainSpec chainSpec, IInitConfig initConfig) : INethermindPlugin
    {
        public string Name => "TestPlugin1";
        public string Description => "TestPlugin1";
        public string Author => "TestPlugin1";

        // Just some arbitrary combination
        public bool Enabled => chainSpec.ChainId == 999 && initConfig.DiscoveryEnabled && !initConfig.PeerManagerEnabled;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private class TestPlugin2() : INethermindPlugin
    {
        public string Name => "TestPlugin2";
        public string Description => "TestPlugin2";
        public string Author => "TestPlugin2";
        public bool Enabled => false;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }


    private class AnotherAura() : IConsensusPlugin
    {
        public string Name => "TestPlugin2";
        public string Description => "TestPlugin2";
        public string Author => "TestPlugin2";
        public bool Enabled => true;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public IBlockProducer InitBlockProducer(ITxSource additionalTxSource = null)
        {
            throw new NotImplementedException();
        }

        public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
        {
            throw new NotImplementedException();
        }
    }
}
