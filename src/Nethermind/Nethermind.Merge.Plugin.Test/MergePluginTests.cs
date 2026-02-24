// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.HealthChecks;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Runner.Ethereum.Modules;
using Nethermind.Runner.Test.Ethereum;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.State.Proofs;
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
            .AddModule(new HealthCheckPluginModule()) // The merge RPC require it.
            .AddSingleton<IBlockProcessingQueue>(Substitute.For<IBlockProcessingQueue>())
            .OnBuild((ctx) =>
            {
                INethermindApi api = ctx.Resolve<INethermindApi>();
                Build.MockOutNethermindApi((NethermindApi)api);

                api.BlockProcessingQueue?.IsEmpty.Returns(true);
            })
            .Build();
    }

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
        Assert.DoesNotThrow(() => _plugin.InitBlockProducer(_consensusPlugin!));
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
        _plugin.InitBlockProducer(_consensusPlugin!);
        Assert.That(api.BlockProducer, Is.InstanceOf<MergeBlockProducer>());
    }

    [Test]
    public async Task Init_registers_gas_limit_calculator_for_testing_rpc_module()
    {
        using IContainer container = BuildContainer();
        INethermindApi api = container.Resolve<INethermindApi>();
        await _consensusPlugin!.Init(api);
        await _plugin.Init(api);

        Assert.DoesNotThrow(() => container.Resolve<IGasLimitCalculator>());
    }

    [Test]
    public async Task Testing_buildBlockV1_sets_excess_blob_gas_for_eip4844()
    {
        Hash256 parentHash = Keccak.Compute("parent");
        BlockHeader parentHeader = new(
            Keccak.Compute("grandparent"),
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            UInt256.Zero,
            1,
            30_000_000,
            1,
            [])
        {
            Hash = parentHash,
            TotalDifficulty = UInt256.Zero,
            BaseFeePerGas = UInt256.One,
            GasUsed = 0,
            StateRoot = Keccak.EmptyTreeHash,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            Bloom = Bloom.Empty,
            BlobGasUsed = 0,
            ExcessBlobGas = 0,
        };

        Block parentBlock = new(parentHeader, Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), Array.Empty<Withdrawal>());
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(parentHash).Returns(parentBlock);

        Hash256? suggestedWithdrawalsRoot = null;
        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
        blockchainProcessor
            .Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
            .Returns(static callInfo =>
            {
                Block block = callInfo.Arg<Block>();
                block.Header.StateRoot ??= Keccak.EmptyTreeHash;
                block.Header.ReceiptsRoot ??= Keccak.EmptyTreeHash;
                block.Header.Bloom ??= Bloom.Empty;
                block.Header.GasUsed = 0;
                block.Header.Hash ??= Keccak.Compute("produced");
                return block;
            });
        blockchainProcessor
            .When(x => x.Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                Block block = callInfo.Arg<Block>();
                suggestedWithdrawalsRoot = block.Header.WithdrawalsRoot;
            });

        IMainProcessingContext mainProcessingContext = Substitute.For<IMainProcessingContext>();
        mainProcessingContext.BlockchainProcessor.Returns(blockchainProcessor);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Osaka.Instance);

        IGasLimitCalculator gasLimitCalculator = Substitute.For<IGasLimitCalculator>();
        gasLimitCalculator.GetGasLimit(Arg.Any<BlockHeader>()).Returns(parentHeader.GasLimit);

        TestingRpcModule module = new(
            mainProcessingContext,
            gasLimitCalculator,
            specProvider,
            blockFinder,
            LimboLogs.Instance);

        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = parentHeader.Timestamp + 12,
            PrevRandao = Keccak.Compute("randao"),
            SuggestedFeeRecipient = Address.Zero,
            Withdrawals =
            [
                new Withdrawal
                {
                    Index = 0,
                    ValidatorIndex = 0,
                    Address = Address.Zero,
                    AmountInGwei = 1
                }
            ],
            ParentBeaconBlockRoot = Keccak.Compute("parentBeaconBlockRoot")
        };

        ResultWrapper<GetPayloadV5Result?> result = await module.testing_buildBlockV1(parentHash, payloadAttributes, Array.Empty<byte[]>(), Array.Empty<byte>());

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Should().NotBeNull();
        result.Data!.ExecutionPayload.BlobGasUsed.Should().Be(0);
        result.Data!.ExecutionPayload.ExcessBlobGas.Should().Be(BlobGasCalculator.CalculateExcessBlobGas(parentHeader, Osaka.Instance));
        suggestedWithdrawalsRoot.Should().Be(new WithdrawalTrie(payloadAttributes.Withdrawals!).RootHash);
    }

    [Test]
    public async Task Testing_buildBlockV1_json_rpc_accepts_omitted_extraData()
    {
        Hash256 parentHash = Keccak.Compute("parent");
        BlockHeader parentHeader = new(
            Keccak.Compute("grandparent"),
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            UInt256.Zero,
            1,
            30_000_000,
            1,
            [])
        {
            Hash = parentHash,
            TotalDifficulty = UInt256.Zero,
            BaseFeePerGas = UInt256.One,
            GasUsed = 0,
            StateRoot = Keccak.EmptyTreeHash,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            Bloom = Bloom.Empty,
            BlobGasUsed = 0,
            ExcessBlobGas = 0,
        };

        Block parentBlock = new(parentHeader, Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), Array.Empty<Withdrawal>());
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(parentHash).Returns(parentBlock);

        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
        blockchainProcessor
            .Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
            .Returns(static callInfo =>
            {
                Block block = callInfo.Arg<Block>();
                block.Header.StateRoot ??= Keccak.EmptyTreeHash;
                block.Header.ReceiptsRoot ??= Keccak.EmptyTreeHash;
                block.Header.Bloom ??= Bloom.Empty;
                block.Header.GasUsed = 0;
                block.Header.Hash ??= Keccak.Compute("produced");
                return block;
            });

        IMainProcessingContext mainProcessingContext = Substitute.For<IMainProcessingContext>();
        mainProcessingContext.BlockchainProcessor.Returns(blockchainProcessor);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Osaka.Instance);

        IGasLimitCalculator gasLimitCalculator = Substitute.For<IGasLimitCalculator>();
        gasLimitCalculator.GetGasLimit(Arg.Any<BlockHeader>()).Returns(parentHeader.GasLimit);

        TestingRpcModule module = new(
            mainProcessingContext,
            gasLimitCalculator,
            specProvider,
            blockFinder,
            LimboLogs.Instance);

        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = parentHeader.Timestamp + 12,
            PrevRandao = Keccak.Compute("randao"),
            SuggestedFeeRecipient = Address.Zero,
            Withdrawals = [],
            ParentBeaconBlockRoot = Keccak.Compute("parentBeaconBlockRoot")
        };

        JsonRpcResponse response = await RpcTest.TestRequest<ITestingRpcModule>(
            module,
            nameof(ITestingRpcModule.testing_buildBlockV1),
            parentHash,
            payloadAttributes,
            Array.Empty<byte[]>());

        response.Should().BeOfType<JsonRpcSuccessResponse>();
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
    [TestCase(false, true, false)]
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
    }
}
