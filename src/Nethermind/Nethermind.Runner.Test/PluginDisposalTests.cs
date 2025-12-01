// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Modules;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[TestFixture]
public class PluginDisposalTests
{
    private ChainSpec _chainSpec = null!;
    private IConsensusPlugin _consensusPlugin = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        IChainSpecParametersProvider provider = Substitute.For<IChainSpecParametersProvider>();
        provider.SealEngineType.Returns(SealEngineType.None);
        provider.AllChainSpecParameters.Returns(Array.Empty<IChainSpecEngineParameters>());

        _chainSpec = new ChainSpec
        {
            Name = "test",
            Parameters = new ChainParameters(),
            SealEngineType = SealEngineType.None,
            EngineChainSpecParametersProvider = provider
        };
    }

    [SetUp]
    public void Setup()
    {
        _consensusPlugin = Substitute.For<IConsensusPlugin>();
        _consensusPlugin.Enabled.Returns(true);
        _consensusPlugin.ApiType.Returns(typeof(NethermindApi));
        IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
        _consensusPlugin.InitBlockProducer().Returns(blockProducer);

        IBlockProducerRunner blockProducerRunner = Substitute.For<IBlockProducerRunner>();
        _consensusPlugin.InitBlockProducerRunner(blockProducer).Returns(blockProducerRunner);
    }

    [Test]
    public void Sync_plugin_is_disposed_when_container_is_disposed()
    {
        INethermindPlugin plugin = CreateSyncPlugin();

        using (BuildContainer(plugin)) { }

        ((IDisposable)plugin).Received(1).Dispose();
    }

    [Test]
    public async Task Async_plugin_is_disposed_when_container_is_disposed_async()
    {
        INethermindPlugin plugin = CreateAsyncPlugin();

        await using (BuildContainer(plugin)) { }
        await ((IAsyncDisposable)plugin).Received(1).DisposeAsync();
    }

    [Test]
    public void Async_plugin_is_disposed_when_container_is_disposed_sync()
    {
        INethermindPlugin plugin = CreateAsyncPlugin();

        using (BuildContainer(plugin)) { }

        _ = ((IAsyncDisposable)plugin).Received(1).DisposeAsync();
    }

    private IContainer BuildContainer(INethermindPlugin plugin) => new ContainerBuilder()
        .AddModule(new NethermindRunnerModule(
            new EthereumJsonSerializer(),
            _chainSpec,
            new ConfigProvider(),
            Substitute.For<IProcessExitSource>(),
            new INethermindPlugin[] { _consensusPlugin, plugin },
            LimboLogs.Instance))
        .Build();

    private static INethermindPlugin CreateSyncPlugin()
    {
        INethermindPlugin plugin = Substitute.For<INethermindPlugin, IDisposable>();
        plugin.Enabled.Returns(true);
        return plugin;
    }

    private static INethermindPlugin CreateAsyncPlugin()
    {
        INethermindPlugin plugin = Substitute.For<INethermindPlugin, IAsyncDisposable>();
        plugin.Enabled.Returns(true);
        return plugin;
    }
}
