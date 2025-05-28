// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Runner.Ethereum.Modules;
using Nethermind.Runner.Test.Ethereum;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Test;

public class MergePluginTests
{
    private ChainSpec _chainSpec = null!;
    private MergeConfig _mergeConfig = null!;
    private IJsonRpcConfig _jsonRpcConfig = null!;
    private MergePlugin _plugin = null!;
    private CliquePlugin? _consensusPlugin = null;

    [SetUp]
    public void Setup()
    {
        _chainSpec = new ChainSpec()
        {
            Parameters = new ChainParameters(),
            SealEngineType = SealEngineType.Clique,
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new CliqueChainSpecEngineParameters { Epoch = CliqueConfig.Default.Epoch, Period = CliqueConfig.Default.BlockPeriod }),
        };
        _mergeConfig = new MergeConfig() { TerminalTotalDifficulty = "0" };
        _jsonRpcConfig = new JsonRpcConfig() { Enabled = true, EnabledModules = [ModuleType.Engine] };
        _plugin = new MergePlugin(_chainSpec, _mergeConfig);
        _consensusPlugin = new(_chainSpec);
    }

    private IContainer BuildContainer(IConfigProvider? configProvider = null)
    {
        return new ContainerBuilder()
            .AddModule(new NethermindRunnerModule(
                new EthereumJsonSerializer(),
                _chainSpec,
                configProvider ?? new ConfigProvider(_mergeConfig, _jsonRpcConfig),
                Substitute.For<IProcessExitSource>(),
                [_consensusPlugin!, _plugin],
                LimboLogs.Instance))
            .AddSingleton<IRpcModuleProvider>(Substitute.For<IRpcModuleProvider>())
            .OnBuild((ctx) =>
            {
                INethermindApi api = ctx.Resolve<INethermindApi>();
                Build.MockOutNethermindApi((NethermindApi)api);

                api.BlockProcessingQueue?.IsEmpty.Returns(true);
                api.DbFactory = new MemDbFactory();
                api.BlockProducerEnvFactory = new BlockProducerEnvFactory(
                    api.WorldStateManager!,
                    api.ReadOnlyTxProcessingEnvFactory,
                    api.BlockTree!,
                    api.SpecProvider!,
                    api.BlockValidator!,
                    api.RewardCalculatorSource!,
                    api.ReceiptStorage!,
                    api.BlockPreprocessor!,
                    api.TxPool!,
                    api.TransactionComparerProvider!,
                    ctx.Resolve<IBlocksConfig>(),
                    api.LogManager!);
                api.EngineRequestsTracker = Substitute.For<IEngineRequestsTracker>();
            })
            .Build();
    }

    [TearDown]
    public void TearDown() => _plugin.DisposeAsync().GetAwaiter().GetResult();

    [Test]
    public void SlotPerSeconds_has_different_value_in_mergeConfig_and_blocksConfig()
    {
        JsonConfigSource? jsonSource = new("MisconfiguredConfig.json");
        ConfigProvider? configProvider = new();
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
        Assert.DoesNotThrow(() => _plugin.InitBlockProducer(_consensusPlugin!, null));
        Assert.DoesNotThrowAsync(async () => await _plugin.InitRpcModules());
        Assert.DoesNotThrowAsync(async () => await _plugin.DisposeAsync());
    }

    [Test]
    public async Task Initializes_correctly()
    {
        using IContainer container = BuildContainer();
        INethermindApi api = container.Resolve<INethermindApi>();
        Assert.DoesNotThrowAsync(async () => await _consensusPlugin!.Init(api));
        await _plugin.Init(api);
        await _plugin.InitNetworkProtocol();
        ISyncConfig syncConfig = api.Config<ISyncConfig>();
        Assert.That(syncConfig.NetworkingEnabled, Is.True);
        Assert.That(api.GossipPolicy.CanGossipBlocks, Is.True);
        _plugin.InitBlockProducer(_consensusPlugin!, null);
        Assert.That(api.BlockProducer, Is.InstanceOf<MergeBlockProducer>());
        await _plugin.InitRpcModules();
        api.RpcModuleProvider!.Received().Register(Arg.Is<IRpcModulePool<IEngineRpcModule>>(m => m is SingletonModulePool<IEngineRpcModule>));
        await _plugin.DisposeAsync();
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
                AdditionalRpcUrls = new[] { "http://localhost:8550|http;ws|net;eth;subscribe;web3;client|no-auth" }
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
        await _plugin.Invoking((plugin) => plugin.Init(api))
            .Should()
            .ThrowAsync<InvalidConfigurationException>();
    }

    [Test]
    public async Task InitDisableJsonRpcUrlWithNoEngineUrl()
    {
        JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
        {
            Enabled = false,
            EnabledModules = new string[] { "eth", "subscribe" },
            AdditionalRpcUrls = new[]
            {
                "http://localhost:8550|http;ws|net;eth;subscribe;web3;client|no-auth",
                "http://localhost:8551|http;ws|net;eth;subscribe;web3;engine;client",
            }
        };

        using IContainer container = BuildContainer(new ConfigProvider(_mergeConfig, jsonRpcConfig));
        INethermindApi api = container.Resolve<INethermindApi>();
        await _plugin.Init(api);

        jsonRpcConfig.Enabled.Should().BeTrue();
        jsonRpcConfig.EnabledModules.Should().BeEquivalentTo([]);
        jsonRpcConfig.AdditionalRpcUrls.Should().BeEquivalentTo(new string[]
        {
            "http://localhost:8551|http;ws|net;eth;subscribe;web3;engine;client"
        });
    }

    [TestCase(true, true, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, true)]
    public async Task InitThrowExceptionIfBodiesAndReceiptIsDisabled(bool downloadBody, bool downloadReceipt, bool shouldPass)
    {
        ISyncConfig syncConfig = new SyncConfig()
        {
            FastSync = true,
            DownloadBodiesInFastSync = downloadBody,
            DownloadReceiptsInFastSync = downloadReceipt
        };

        using IContainer container = BuildContainer(new ConfigProvider(_mergeConfig, _jsonRpcConfig, syncConfig));
        INethermindApi api = container.Resolve<INethermindApi>();
        Func<Task>? invocation = _plugin.Invoking((plugin) => plugin.Init(api));
        if (shouldPass)
        {
            await invocation.Should().NotThrowAsync();
        }
        else
        {
            await invocation.Should().ThrowAsync<InvalidConfigurationException>();
        }

        if (!downloadBody && downloadReceipt)
        {
            syncConfig.DownloadBodiesInFastSync.Should().BeTrue(); // Modified by PruningTrieStateFactory
        }
    }
}
