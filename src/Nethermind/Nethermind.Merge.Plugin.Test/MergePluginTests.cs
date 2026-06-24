// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Runner.Ethereum.Modules;
using Nethermind.Runner.Test.Ethereum;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.Stats.Model;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Test;

public class MergePluginTests
{
    private sealed class SourceGenProbe
    {
        public int Value { get; set; }
    }

    private sealed class PriorityProbe
    {
        public int Value { get; set; }
    }

    private sealed class ThrowingProbeResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            type == typeof(SourceGenProbe) ? throw new InvalidOperationException("probe resolver was used") : null;
    }

    private sealed class RecordingProbeResolver(string name, List<string> calls) : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type == typeof(PriorityProbe))
            {
                calls.Add(name);
            }

            return null;
        }
    }

    private ChainSpec _chainSpec = null!;
    private MergeConfig _mergeConfig = null!;
    private IJsonRpcConfig _jsonRpcConfig = null!;
    private MergePlugin _plugin = null!;
    private CliquePlugin? _consensusPlugin;

    [SetUp]
    public void Setup()
    {
        _chainSpec = new ChainSpec
        {
            Parameters = new ChainParameters(),
            SealEngineType = SealEngineType.Clique,
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new CliqueChainSpecEngineParameters { Epoch = CliqueConfig.Default.Epoch, Period = CliqueConfig.Default.BlockPeriod }),
        };
        _mergeConfig = new MergeConfig { TerminalTotalDifficulty = "0" };
        _jsonRpcConfig = new JsonRpcConfig { Enabled = true, EnabledModules = [ModuleType.Engine] };
        _plugin = new MergePlugin(_chainSpec, _mergeConfig);
        _consensusPlugin = new(_chainSpec);
    }

    private IContainer BuildContainer(IConfigProvider? configProvider = null, Action<ContainerBuilder>? configure = null)
    {
        // HealthCheckPluginModule first: mirrors PluginConfig.PluginOrder (HealthChecks < Merge).
        // BaseMergePluginModule must not override the real ClHealthRequestsTracker binding.
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new HealthCheckPluginModule())
            .AddModule(new NethermindRunnerModule(
                new EthereumJsonSerializer(),
                _chainSpec,
                configProvider ?? new ConfigProvider(_mergeConfig, _jsonRpcConfig),
                Substitute.For<IProcessExitSource>(),
                [_consensusPlugin!, _plugin],
                LimboLogs.Instance))
            .AddSingleton(Substitute.For<IRpcModuleProvider>())
            .AddSingleton(Substitute.For<IBlockProcessingQueue>())
            .AddSingleton(Substitute.For<IProtocolsManager>())
            .OnBuild(ctx =>
            {
                INethermindApi api = ctx.Resolve<INethermindApi>();
                Build.MockOutNethermindApi((NethermindApi)api);

                ctx.Resolve<IBlockProcessingQueue>().IsEmpty.Returns(true);
            });

        configure?.Invoke(builder);

        return builder.Build();
    }

    [Test]
    public void EngineRequestsTracker_resolves_to_ClHealthRequestsTracker_when_HealthChecks_loaded_first()
    {
        using IContainer container = BuildContainer();

        Assert.That(container.Resolve<IEngineRequestsTracker>(), Is.TypeOf<ClHealthRequestsTracker>());
    }

    [Test]
    public void SlotPerSeconds_has_different_value_in_mergeConfig_and_blocksConfig()
    {
        JsonConfigSource jsonSource = new("MisconfiguredConfig.json");
        ConfigProvider configProvider = new();
        configProvider.AddSource(jsonSource);
        configProvider.Initialize();
        IBlocksConfig blocksConfig = configProvider.GetConfig<IBlocksConfig>();
        IMergeConfig mergeConfig = configProvider.GetConfig<IMergeConfig>();
        Assert.Throws<InvalidConfigurationException>(() =>
        {
            MergePlugin.MigrateSecondsPerSlot(blocksConfig, mergeConfig);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Init_merge_plugin_does_not_throw_exception(bool enabled)
    {
        using IContainer container = BuildContainer();
        INethermindApi api = container.Resolve<INethermindApi>();
        _mergeConfig.TerminalTotalDifficulty = enabled ? "0" : null;
        Assert.DoesNotThrowAsync(async () => await _consensusPlugin!.Init(api));
        Assert.DoesNotThrowAsync(async () => await _plugin.Init(api));
        Assert.DoesNotThrowAsync(async () => await _plugin.InitNetworkProtocol());
        Assert.DoesNotThrow(() => container.Resolve<IBlockProducerFactory>().InitBlockProducer());
    }

    [Test]
    public void AddTypeInfoResolver_updates_existing_serializer_instances()
    {
        EthereumJsonSerializer serializer = new();
        EthereumJsonSerializer.AddTypeInfoResolver(new ThrowingProbeResolver());

        Assert.Throws<InvalidOperationException>(() => serializer.Serialize(new SourceGenProbe { Value = 1 }));
    }

    [Test]
    public void AddTypeInfoResolver_orders_resolvers_by_priority_then_registration_order()
    {
        List<string> calls = [];

        EthereumJsonSerializer.AddTypeInfoResolver(new RecordingProbeResolver("external", calls));
        EthereumJsonSerializer.AddTypeInfoResolver(new RecordingProbeResolver("json-rpc-response", calls), JsonTypeInfoResolverPriority.JsonRpcResponse);
        EthereumJsonSerializer.AddTypeInfoResolver(new RecordingProbeResolver("engine-api", calls), JsonTypeInfoResolverPriority.EngineApi);
        EthereumJsonSerializer.AddTypeInfoResolver(new RecordingProbeResolver("facade-first", calls), JsonTypeInfoResolverPriority.Facade);
        EthereumJsonSerializer.AddTypeInfoResolver(new RecordingProbeResolver("facade-second", calls), JsonTypeInfoResolverPriority.Facade);

        _ = EthereumJsonSerializer.JsonOptions.GetTypeInfo(typeof(PriorityProbe));

        Assert.That(calls, Is.EqualTo(new[]
        {
            "engine-api",
            "facade-first",
            "facade-second",
            "json-rpc-response",
            "external"
        }));
    }

    [Test]
    public async Task Initializes_correctly()
    {
        await using IContainer container = BuildContainer();
        INethermindApi api = container.Resolve<INethermindApi>();
        Assert.DoesNotThrowAsync(async () => await _consensusPlugin!.Init(api));
        await _plugin.Init(api);
        await _plugin.InitNetworkProtocol();
        ISyncConfig syncConfig = api.Config<ISyncConfig>();
        Assert.That(syncConfig.NetworkingEnabled, Is.True);
        Assert.That(api.GossipPolicy.CanGossipBlocks, Is.True);
        IBlockProducer blockProducer = container.Resolve<IBlockProducerFactory>().InitBlockProducer();
        Assert.That(blockProducer, Is.InstanceOf<MergeBlockProducer>());
        Assert.That(container.Resolve<IBlockProductionPolicy>(), Is.InstanceOf<MergeBlockProductionPolicy>());
    }

    [Test]
    public async Task InitNetworkProtocol_adds_post_merge_eth_capabilities_when_transition_finished()
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TransitionFinished.Returns(true);

        await using IContainer container = BuildContainer(configure: builder => builder
            .RegisterInstance(poSSwitcher)
            .As<IPoSSwitcher>());
        INethermindApi api = container.Resolve<INethermindApi>();
        await _consensusPlugin!.Init(api);
        await _plugin.Init(api);

        api.ProtocolsManager!.ClearReceivedCalls();
        await _plugin.InitNetworkProtocol();

        AssertPostMergeEthCapabilitiesAdded(api);
    }

    [Test]
    public async Task InitNetworkProtocol_delays_post_merge_eth_capabilities_until_terminal_block()
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TransitionFinished.Returns(false);

        await using IContainer container = BuildContainer(configure: builder => builder
            .RegisterInstance(poSSwitcher)
            .As<IPoSSwitcher>());
        INethermindApi api = container.Resolve<INethermindApi>();
        await _consensusPlugin!.Init(api);
        await _plugin.Init(api);

        api.ProtocolsManager!.ClearReceivedCalls();
        await _plugin.InitNetworkProtocol();

        api.ProtocolsManager!.DidNotReceive().AddSupportedCapability(Arg.Any<Capability>());

        poSSwitcher.TerminalBlockReached += Raise.Event();

        AssertPostMergeEthCapabilitiesAdded(api);
    }

    [Test]
    public async Task Init_registers_gas_limit_calculator_for_testing_rpc_module()
    {
        await using IContainer container = BuildContainer();
        INethermindApi api = container.Resolve<INethermindApi>();
        await _consensusPlugin!.Init(api);
        await _plugin.Init(api);

        Assert.DoesNotThrow(() => container.Resolve<IGasLimitCalculator>());
    }

    [TestCase(true, true)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(false, false)]
    public async Task InitThrowsWhenNoEngineApiUrlsConfigured(bool jsonRpcEnabled, bool configuredViaAdditionalUrls)
    {
        IJsonRpcConfig jsonRpcConfig;
        if (configuredViaAdditionalUrls)
        {
            jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = jsonRpcEnabled,
                AdditionalRpcUrls = ["http://localhost:8550|http;ws|net;eth;subscribe;web3;client|no-auth"]
            };
        }
        else
        {
            jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = jsonRpcEnabled
            };
        }

        using IContainer container = BuildContainer(new ConfigProvider(_mergeConfig, jsonRpcConfig));
        INethermindApi api = container.Resolve<INethermindApi>();
        Assert.That(async () => await _plugin.Init(api), Throws.TypeOf<InvalidConfigurationException>());
    }

    [Test]
    public async Task InitDisableJsonRpcUrlWithNoEngineUrl()
    {
        JsonRpcConfig jsonRpcConfig = new()
        {
            Enabled = false,
            EnabledModules = ["eth", "subscribe"],
            AdditionalRpcUrls =
            [
                "http://localhost:8550|http;ws|net;eth;subscribe;web3;client|no-auth",
                "http://localhost:8551|http;ws|net;eth;subscribe;web3;engine;client"
            ]
        };

        await using IContainer container = BuildContainer(new ConfigProvider(_mergeConfig, jsonRpcConfig));
        INethermindApi api = container.Resolve<INethermindApi>();
        await _plugin.Init(api);

        Assert.That(jsonRpcConfig.Enabled, Is.True);
        Assert.That(jsonRpcConfig.EnabledModules, Is.Empty);
        Assert.That(jsonRpcConfig.AdditionalRpcUrls, Is.EqualTo(new[] { "http://localhost:8551|http;ws|net;eth;subscribe;web3;engine;client" }));
    }

    private static void AssertPostMergeEthCapabilitiesAdded(INethermindApi api)
    {
        IProtocolsManager protocolsManager = api.ProtocolsManager!;

        protocolsManager.Received(1).AddSupportedCapability(new Capability(Protocol.Eth, 69));
        protocolsManager.Received(1).AddSupportedCapability(new Capability(Protocol.Eth, 70));
        protocolsManager.Received(1).AddSupportedCapability(new Capability(Protocol.Eth, 71));
    }
}
