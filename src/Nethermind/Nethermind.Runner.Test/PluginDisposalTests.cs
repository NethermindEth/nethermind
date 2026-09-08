// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Modules;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[TestFixture]
public class PluginDisposalTests
{
    private IConsensusPlugin _consensusPlugin = null!;

    [SetUp]
    public void Setup()
    {
        SubstitutionContext.Current?.ThreadContext?.DequeueAllArgumentSpecifications();
        _consensusPlugin = Substitute.For<IConsensusPlugin>();
        _consensusPlugin.ApiType.Returns(typeof(NethermindApi));
    }

    [Test]
    public void Sync_plugin_is_disposed_when_container_is_disposed()
    {
        INethermindPlugin plugin = Substitute.For<INethermindPlugin, IDisposable>();

        using (BuildContainer(plugin)) { }

        ((IDisposable)plugin).Received(1).Dispose();
    }

    [Test]
    public async Task Async_plugin_is_disposed_when_container_is_disposed_async()
    {
        INethermindPlugin plugin = Substitute.For<INethermindPlugin, IAsyncDisposable>();

        await using (BuildContainer(plugin)) { }

        await ((IAsyncDisposable)plugin).Received(1).DisposeAsync();
    }

    [Test]
    public void Async_plugin_is_disposed_when_container_is_disposed_sync()
    {
        INethermindPlugin plugin = Substitute.For<INethermindPlugin, IAsyncDisposable>();

        using (BuildContainer(plugin)) { }

        _ = ((IAsyncDisposable)plugin).Received(1).DisposeAsync();
    }

    private IContainer BuildContainer(INethermindPlugin plugin) => new ContainerBuilder()
        .AddModule(new NethermindRunnerModule(
            new EthereumJsonSerializer(),
            new ChainSpec
            {
                Name = "test",
                Parameters = new ChainParameters(),
                SealEngineType = SealEngineType.NethDev,
                EngineChainSpecParametersProvider = Substitute.For<IChainSpecParametersProvider>(),
            },
            new ConfigProvider(),
            Substitute.For<IProcessExitSource>(),
            new INethermindPlugin[] { _consensusPlugin, plugin },
            LimboLogs.Instance))
        .Build();
}
