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
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Ethash;
using Nethermind.HealthChecks;
using Nethermind.Hive;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Mev;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Api.Test;

public class PluginLoaderTests
{
    [Test]
    public void full_lexicographical_order()
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IPluginLoader loader = new PluginLoader("", fileSystem, typeof(AuRaPlugin), typeof(CliquePlugin),
            typeof(EthashPlugin), typeof(NethDevPlugin), typeof(HivePlugin), typeof(TestPlugin));
        loader.Load(new TestLogManager());
        loader.OrderPlugins(new PluginConfig {PluginOrder = Array.Empty<string>()});
        var expected = new List<Type>
        {
            typeof(AuRaPlugin),
            typeof(CliquePlugin),
            typeof(EthashPlugin),
            typeof(NethDevPlugin),
            typeof(HivePlugin),
            typeof(TestPlugin)
        };
        CollectionAssert.AreEqual(expected, loader.PluginTypes.ToList());
    }

    [Test]
    public void full_order()
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IPluginLoader loader = new PluginLoader("", fileSystem, typeof(AuRaPlugin), typeof(CliquePlugin),
            typeof(EthashPlugin), typeof(NethDevPlugin), typeof(HivePlugin), typeof(TestPlugin));
        loader.Load(new TestLogManager());
        IPluginConfig pluginConfig =
            new PluginConfig {PluginOrder = new[] {"Hive", "TestPlugin", "NethDev", "Ethash", "Clique", "Aura"}};
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
        CollectionAssert.AreEqual(expected, loader.PluginTypes.ToList());
    }

    [Test]
    public void partial_lexicographical_order()
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IPluginLoader loader = new PluginLoader("", fileSystem, typeof(AuRaPlugin), typeof(CliquePlugin),
            typeof(EthashPlugin), typeof(NethDevPlugin), typeof(HivePlugin), typeof(TestPlugin));
        loader.Load(new TestLogManager());
        IPluginConfig pluginConfig =
            new PluginConfig() {PluginOrder = new[] {"Hive", "NethDev", "Ethash"}};
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
        CollectionAssert.AreEqual(expected, loader.PluginTypes.ToList());
    }
    
    [Test]
    public void default_config()
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IPluginLoader loader = new PluginLoader("", fileSystem,
            typeof(EthashPlugin), typeof(NethDevPlugin), typeof(HivePlugin), typeof(HealthChecksPlugin),
            typeof(MergePlugin), typeof(MevPlugin));
        loader.Load(new TestLogManager());
        IPluginConfig pluginConfig =
            new PluginConfig();
        loader.OrderPlugins(pluginConfig);

        var expected = new List<Type>
        {
            typeof(EthashPlugin),
            typeof(NethDevPlugin),
            typeof(MergePlugin),
            typeof(MevPlugin),
            typeof(HealthChecksPlugin),
            typeof(HivePlugin)
        };
        CollectionAssert.AreEqual(expected, loader.PluginTypes.ToList());
    }
}
