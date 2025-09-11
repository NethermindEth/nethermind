// using System;
// using System.Collections.Generic;
// using System.IO.Abstractions;
// using System.Linq;
// using System.Threading.Tasks;
// using FluentAssertions;
// using Nethermind.Api.Extensions;
// using Nethermind.Config;
// using Nethermind.Consensus;
// using Nethermind.Consensus.AuRa;
// using Nethermind.Consensus.Clique;
// using Nethermind.Consensus.Ethash;
// using Nethermind.Core;
// using Nethermind.HealthChecks;
// using Nethermind.Hive;
// using Nethermind.Logging;
// using Nethermind.Merge.Plugin;
// using Nethermind.Specs.ChainSpecStyle;
// using NSubstitute;
// using NUnit.Framework;
//
// namespace Nethermind.Api.Test;
//
// public class PluginLoaderTests
// {
//     [Test]
//     public void full_lexicographical_order()
//     {
//         IFileSystem fileSystem = Substitute.For<IFileSystem>();
//         var loader = new PluginLoader(string.Empty, fileSystem, new TestLogManager().GetClassLogger(),
//             typeof(AuRaPlugin),
//             typeof(CliquePlugin),
//             typeof(EthashPlugin),
//             typeof(NethDevPlugin),
//             typeof(HivePlugin),
//             typeof(TestPlugin));
//         loader.Load();
//
//         var plugins = loader.PluginTypes.Select(t => (INethermindPlugin)Activator.CreateInstance(t)!).ToList();
//         var pluginOrder = new PluginConfig { PluginOrder = Array.Empty<string>() }.PluginOrder;
//
//         var orderedPlugins = loader.OrderPlugins(plugins, pluginOrder);
//
//         var expected = new List<Type>
//         {
//             typeof(AuRaPlugin),
//             typeof(CliquePlugin),
//             typeof(EthashPlugin),
//             typeof(NethDevPlugin),
//             typeof(HivePlugin),
//             typeof(TestPlugin)
//         };
//
//         Assert.That(orderedPlugins.Select(p => p.GetType()).ToList(), Is.EqualTo(expected).AsCollection);
//     }
//
//     [Test]
//     public void full_order()
//     {
//         IFileSystem fileSystem = Substitute.For<IFileSystem>();
//         var loader = new PluginLoader(string.Empty, fileSystem, new TestLogManager().GetClassLogger(),
//             typeof(AuRaPlugin),
//             typeof(CliquePlugin),
//             typeof(EthashPlugin),
//             typeof(NethDevPlugin),
//             typeof(HivePlugin),
//             typeof(TestPlugin));
//         loader.Load();
//
//         var plugins = loader.PluginTypes.Select(t => (INethermindPlugin)Activator.CreateInstance(t)!).ToList();
//         var pluginOrder = new PluginConfig { PluginOrder = new[] { "Hive", "TestPlugin", "NethDev", "Ethash", "Clique", "Aura" } }.PluginOrder;
//
//         var orderedPlugins = loader.OrderPlugins(plugins, pluginOrder);
//
//         var expected = new List<Type>
//         {
//             typeof(NethDevPlugin),
//             typeof(EthashPlugin),
//             typeof(CliquePlugin),
//             typeof(AuRaPlugin),
//             typeof(HivePlugin),
//             typeof(TestPlugin)
//         };
//
//         Assert.That(orderedPlugins.Select(p => p.GetType()).ToList(), Is.EqualTo(expected).AsCollection);
//     }
//
//     [Test]
//     public void throws_when_multiple_consensus_plugin()
//     {
//         IFileSystem fileSystem = Substitute.For<IFileSystem>();
//         var loader = new PluginLoader(
//             string.Empty,
//             fileSystem,
//             new TestLogManager().GetClassLogger(),
//             typeof(AuRaPlugin),
//             typeof(AnotherAura),
//             typeof(CliquePlugin),
//             typeof(EthashPlugin),
//             typeof(NethDevPlugin),
//             typeof(HivePlugin),
//             typeof(TestPlugin));
//         loader.Load();
//
//         var plugins = loader.PluginTypes.Select(t => (INethermindPlugin)Activator.CreateInstance(t)!).ToList();
//         var pluginOrder = new PluginConfig { PluginOrder = Array.Empty<string>() }.PluginOrder;
//         loader.OrderPlugins(plugins, pluginOrder);
//
//         IConfigProvider configProvider = new ConfigProvider();
//         ChainSpec chainSpec = new ChainSpec();
//         chainSpec.SealEngineType = SealEngineType.AuRa;
//
//         Assert.ThrowsAsync<InvalidOperationException>(async () => await loader.LoadPlugins(configProvider, chainSpec));
//     }
//
//     [Test]
//     public void partial_lexicographical_order()
//     {
//         IFileSystem fileSystem = Substitute.For<IFileSystem>();
//         var loader = new PluginLoader(string.Empty, fileSystem, new TestLogManager().GetClassLogger(),
//             typeof(AuRaPlugin), typeof(CliquePlugin), typeof(EthashPlugin), typeof(NethDevPlugin), typeof(HivePlugin), typeof(TestPlugin));
//         loader.Load();
//
//         var plugins = loader.PluginTypes.Select(t => (INethermindPlugin)Activator.CreateInstance(t)!).ToList();
//         var pluginOrder = new PluginConfig { PluginOrder = new[] { "Hive", "NethDev", "Ethash" } }.PluginOrder;
//
//         var orderedPlugins = loader.OrderPlugins(plugins, pluginOrder);
//
//         var expected = new List<Type>
//         {
//             typeof(NethDevPlugin),
//             typeof(EthashPlugin),
//             typeof(AuRaPlugin),
//             typeof(CliquePlugin),
//             typeof(HivePlugin),
//             typeof(TestPlugin)
//         };
//
//         Assert.That(orderedPlugins.Select(p => p.GetType()).ToList(), Is.EqualTo(expected).AsCollection);
//     }
//
//     [Test]
//     public void default_config()
//     {
//         IFileSystem fileSystem = Substitute.For<IFileSystem>();
//         var loader = new PluginLoader(string.Empty, fileSystem, new TestLogManager().GetClassLogger(),
//             typeof(EthashPlugin), typeof(NethDevPlugin), typeof(HivePlugin), typeof(HealthChecksPlugin), typeof(MergePlugin));
//         loader.Load();
//
//         var plugins = loader.PluginTypes.Select(t => (INethermindPlugin)Activator.CreateInstance(t)).ToList();
//         var pluginOrder = new PluginConfig().PluginOrder;
//
//         var orderedPlugins = loader.OrderPlugins(plugins, pluginOrder);
//
//         var expected = new List<Type>
//         {
//             typeof(EthashPlugin),
//             typeof(NethDevPlugin),
//             typeof(MergePlugin),
//             typeof(HealthChecksPlugin),
//             typeof(HivePlugin)
//         };
//
//         Assert.That(orderedPlugins.Select(p => p.GetType()).ToList(), Is.EqualTo(expected).AsCollection);
//     }
//
//     [Test]
//     public async Task Can_PassInConfig_And_OnlyLoadEnabledPlugins()
//     {
//         var loader = new PluginLoader(string.Empty, Substitute.For<IFileSystem>(), new TestLogManager().GetClassLogger(),
//             typeof(TestPlugin1), typeof(TestPlugin2));
//         loader.Load();
//
//         IConfigProvider configProvider = new ConfigProvider();
//         IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
//         initConfig.DiscoveryEnabled = true;
//         initConfig.PeerManagerEnabled = false;
//         ChainSpec chainSpec = new ChainSpec();
//         chainSpec.ChainId = 999;
//
//         IList<INethermindPlugin> loadedPlugins = await loader.LoadPlugins(configProvider, chainSpec);
//         loadedPlugins.Should().BeEquivalentTo(new[] { new TestPlugin1(chainSpec, initConfig) });
//     }
//
//     private class TestPlugin1 : INethermindPlugin
//     {
//         private readonly ChainSpec _chainSpec;
//         private readonly IInitConfig _initConfig;
//
//         public TestPlugin1(ChainSpec chainSpec, IInitConfig initConfig)
//         {
//             _chainSpec = chainSpec;
//             _initConfig = initConfig;
//         }
//
//         public string Name => "TestPlugin1";
//         public string Description => "TestPlugin1";
//         public string Author => "TestPlugin1";
//
//         // Just some arbitrary combination
//         public bool Enabled => _chainSpec.ChainId == 999 && _initConfig.DiscoveryEnabled && !_initConfig.PeerManagerEnabled;
//     }
//
//     private class TestPlugin2 : INethermindPlugin
//     {
//         public string Name => "TestPlugin2";
//         public string Description => "TestPlugin2";
//         public string Author => "TestPlugin2";
//         public bool Enabled => false;
//     }
//
//     private class AnotherAura : IConsensusPlugin
//     {
//         public string Name => "TestPlugin2";
//         public string Description => "TestPlugin2";
//         public string Author => "TestPlugin2";
//         public bool Enabled => true;
//
//         public IBlockProducer InitBlockProducer()
//         {
//             throw new NotImplementedException();
//         }
//
//         public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
//         {
//             throw new NotImplementedException();
//         }
//     }
// }
