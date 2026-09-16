// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO.Abstractions;
using NSubstitute;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Pbt;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class BuiltInPluginsTests
{
    [Test]
    public async Task Scheduled_pbt_is_not_silently_disabled_by_plugin_loader([Values] bool pbtEnabled)
    {
        ConfigProvider configs = new(
            new PbtConfig { Enabled = pbtEnabled, MigrationGenesisBootstrap = true },
            new FlatDbConfig { Enabled = true, Layout = FlatLayout.Flat },
            new BlocksConfig { PreWarming = PreWarmMode.None },
            new InitConfig());
        ChainSpec chainSpec = new()
        {
            Genesis = Build.A.Block.WithTimestamp(10).TestObject,
            Parameters = new ChainParameters { Eip8347TransitionTimestamp = 100, Eip7928TransitionTimestamp = 10, Eip6780TransitionTimestamp = 10 }
        };
        PluginLoader loader = new("plugins", Substitute.For<IFileSystem>(), LimboLogs.Instance.GetClassLogger<BuiltInPluginsTests>(), [typeof(PbtPlugin)]);
        loader.Load();
        IList<INethermindPlugin> plugins = await loader.LoadPlugins(configs, chainSpec);
        Assert.That(plugins, Has.Count.EqualTo(1));
        if (pbtEnabled) Assert.That(plugins[0].Module, Is.Not.Null);
        else Assert.Throws<InvalidConfigurationException>(() => _ = plugins[0].Module);
    }

    [Test]
    public void EnsureAllBuiltInPluginsArePresent()
    {
        List<Type> pluginInAssembly = TypeDiscovery.FindNethermindBasedTypes(typeof(INethermindPlugin)).ToList();
        pluginInAssembly.Remove(typeof(IConsensusPlugin));

        HashSet<Type> builtInPlugins = NethermindPlugins.EmbeddedPlugins.ToHashSet();
        foreach (Type type in pluginInAssembly)
        {
            Assert.That(builtInPlugins, Does.Contain(type));
        }
    }
}
