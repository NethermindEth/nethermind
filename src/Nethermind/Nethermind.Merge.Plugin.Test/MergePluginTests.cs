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
using Nethermind.Db.Blooms;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Network.Config;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;
using NSubstitute;
using Build = Nethermind.Runner.Test.Ethereum.Build;

namespace Nethermind.Merge.Plugin.Test;

public class MergePluginTests
{
    private MergeConfig _mergeConfig = null!;
    private INethermindApi _context = null!;
    private MergePlugin _plugin = null!;
    private CliquePlugin? _consensusPlugin = null;

    [SetUp]
    public void Setup()
    {
        _mergeConfig = new MergeConfig() { TerminalTotalDifficulty = "0" };
        BlocksConfig? miningConfig = new();
        IJsonRpcConfig jsonRpcConfig = new JsonRpcConfig() { Enabled = true, EnabledModules = new[] { ModuleType.Engine } };

        ChainSpec chainSpec = new ChainSpec();
        chainSpec.SealEngineType = SealEngineType.Clique;
        chainSpec.Clique = new CliqueParameters()
        {
            Epoch = CliqueConfig.Default.Epoch,
            Period = CliqueConfig.Default.BlockPeriod
        };

        IConfigProvider configProvider = Substitute.For<IConfigProvider>();
        configProvider.GetConfig<IMergeConfig>().Returns(_mergeConfig);
        configProvider.GetConfig<ISyncConfig>().Returns(new SyncConfig());
        configProvider.GetConfig<IBlocksConfig>().Returns(miningConfig);
        configProvider.GetConfig<IJsonRpcConfig>().Returns(jsonRpcConfig);
        configProvider.GetConfig<IBloomConfig>().Returns(new BloomConfig());
        configProvider.GetConfig<INetworkConfig>().Returns(new NetworkConfig());

        ContainerBuilder containerBuilder = Build.BasicTestContainerBuilder();
        containerBuilder.RegisterInstance(configProvider);
        containerBuilder.RegisterInstance(chainSpec);

        IContainer container = containerBuilder.Build();

        _context = Build.ContextWithMocks(container);
        _context.BlockProcessingQueue?.IsEmpty.Returns(true);
        _context.BlockProducerEnvFactory = new BlockProducerEnvFactory(
            _context.WorldStateManager!,
            _context.BlockTree!,
            _context.SpecProvider!,
            _context.BlockValidator!,
            _context.RewardCalculatorSource!,
            _context.ReceiptStorage!,
            _context.BlockPreprocessor!,
            _context.TxPool!,
            _context.TransactionComparerProvider!,
            miningConfig,
            _context.LogManager!);
        _plugin = new MergePlugin(_context.ChainSpec, _mergeConfig);

        _consensusPlugin = new(_context.ChainSpec);
    }

    [TearDown]
    public void TearDown() => _plugin.DisposeAsync().GetAwaiter().GetResult();

    [Test]
    public void SlotPerSeconds_has_different_value_in_mergeConfig_and_blocksConfig()
    {

        JsonConfigSource? jsonSource = new("MisconfiguredConfig.cfg");
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
        _mergeConfig.TerminalTotalDifficulty = enabled ? "0" : null;
        Assert.DoesNotThrowAsync(async () => await _consensusPlugin!.Init(_context));
        Assert.DoesNotThrowAsync(async () => await _plugin.Init(_context));
        Assert.DoesNotThrowAsync(async () => await _plugin.InitNetworkProtocol());
        Assert.DoesNotThrowAsync(async () => await _plugin.InitSynchronization());
        Assert.DoesNotThrowAsync(async () => await _plugin.InitBlockProducer(_consensusPlugin!));
        Assert.DoesNotThrowAsync(async () => await _plugin.InitRpcModules());
        Assert.DoesNotThrowAsync(async () => await _plugin.DisposeAsync());
    }

    [Test]
    public async Task Initializes_correctly()
    {
        Assert.DoesNotThrowAsync(async () => await _consensusPlugin!.Init(_context));
        await _plugin.Init(_context);
        await _plugin.InitSynchronization();
        await _plugin.InitNetworkProtocol();
        ISyncConfig syncConfig = _context.Config<ISyncConfig>();
        Assert.IsTrue(syncConfig.NetworkingEnabled);
        Assert.IsTrue(_context.GossipPolicy.CanGossipBlocks);
        await _plugin.InitBlockProducer(_consensusPlugin!);
        Assert.IsInstanceOf<MergeBlockProducer>(_context.BlockProducer);
        await _plugin.InitRpcModules();
        _context.RpcModuleProvider!.Received().Register(Arg.Is<IRpcModulePool<IEngineRpcModule>>(m => m is SingletonModulePool<IEngineRpcModule>));
        await _plugin.DisposeAsync();
    }

    [TestCase(true, true)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(false, false)]
    public async Task InitThrowsWhenNoEngineApiUrlsConfigured(bool jsonRpcEnabled, bool configuredViaAdditionalUrls)
    {
        if (configuredViaAdditionalUrls)
        {
            _context.ConfigProvider.GetConfig<IJsonRpcConfig>().Returns(new JsonRpcConfig()
            {
                Enabled = jsonRpcEnabled,
                AdditionalRpcUrls = new[] { "http://localhost:8550|http;ws|net;eth;subscribe;web3;client|no-auth" }
            });
        }
        else
        {
            _context.ConfigProvider.GetConfig<IJsonRpcConfig>().Returns(new JsonRpcConfig()
            {
                Enabled = jsonRpcEnabled
            });
        }

        await _plugin.Invoking((plugin) => plugin.Init(_context))
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
        _context.ConfigProvider.GetConfig<IJsonRpcConfig>().Returns(jsonRpcConfig);

        await _plugin.Init(_context);

        jsonRpcConfig.Enabled.Should().BeTrue();
        jsonRpcConfig.EnabledModules.Should().BeEquivalentTo(new string[] { });
        jsonRpcConfig.AdditionalRpcUrls.Should().BeEquivalentTo(new string[]
        {
            "http://localhost:8551|http;ws|net;eth;subscribe;web3;engine;client"
        });
    }

    [TestCase(true, true, true)]
    [TestCase(true, false, false)]
    public async Task InitThrowExceptionIfBodiesAndReceiptIsDisabled(bool downloadBody, bool downloadReceipt, bool shouldPass)
    {
        _context.ConfigProvider.GetConfig<ISyncConfig>().Returns(new SyncConfig()
        {
            FastSync = true,
            DownloadBodiesInFastSync = downloadBody,
            DownloadReceiptsInFastSync = downloadReceipt
        });

        Func<Task>? invocation = _plugin.Invoking((plugin) => plugin.Init(_context));
        if (shouldPass)
        {
            await invocation.Should().NotThrowAsync();
        }
        else
        {
            await invocation.Should().ThrowAsync<InvalidConfigurationException>();
        }
    }
}
