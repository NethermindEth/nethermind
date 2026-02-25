// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization.Metadata;
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
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
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
using System.Text.Json;
using Nethermind.Serialization.Rlp;
using CoreBuild = Nethermind.Core.Test.Builders.Build;
using RunnerBuild = Nethermind.Runner.Test.Ethereum.Build;

namespace Nethermind.Merge.Plugin.Test;

public class MergePluginTests
{
    private sealed class SourceGenProbe
    {
        public int Value { get; set; }
    }

    private sealed class ThrowingProbeResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            type == typeof(SourceGenProbe) ? throw new InvalidOperationException("probe resolver was used") : null;
    }

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
                RunnerBuild.MockOutNethermindApi((NethermindApi)api);

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
    public void AddTypeInfoResolver_updates_existing_serializer_instances()
    {
        EthereumJsonSerializer serializer = new();
        EthereumJsonSerializer.AddTypeInfoResolver(new ThrowingProbeResolver());

        Assert.Throws<InvalidOperationException>(() => serializer.Serialize(new SourceGenProbe { Value = 1 }));
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

        ResultWrapper<object?> result = await module.testing_buildBlockV1(parentHash, payloadAttributes, Array.Empty<byte[]>(), Array.Empty<byte>(), targetFork: "osaka");

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Should().NotBeNull();
        result.Data.Should().BeOfType<GetPayloadV5Result>();
        GetPayloadV5Result payloadResult = (GetPayloadV5Result)result.Data!;
        payloadResult.ExecutionPayload.BlobGasUsed.Should().Be(0);
        payloadResult.ExecutionPayload.ExcessBlobGas.Should().Be(BlobGasCalculator.CalculateExcessBlobGas(parentHeader, Osaka.Instance));
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

    [Test]
    public async Task Testing_buildBlockV1_returns_block_access_list_and_slot_number_for_amsterdam()
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
            SlotNumber = 1
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
                block.BlockAccessList = new BlockAccessList();
                return block;
            });

        IMainProcessingContext mainProcessingContext = Substitute.For<IMainProcessingContext>();
        mainProcessingContext.BlockchainProcessor.Returns(blockchainProcessor);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Amsterdam.Instance);

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
            ParentBeaconBlockRoot = Keccak.Compute("parentBeaconBlockRoot"),
            SlotNumber = 2
        };

        ResultWrapper<object?> result = await module.testing_buildBlockV1(parentHash, payloadAttributes, Array.Empty<byte[]>(), Array.Empty<byte>(), targetFork: "amsterdam");

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Should().BeOfType<GetPayloadV6Result>();
        GetPayloadV6Result payloadResult = (GetPayloadV6Result)result.Data!;
        payloadResult.ExecutionPayload.SlotNumber.Should().Be(payloadAttributes.SlotNumber);
        payloadResult.ExecutionPayload.BlockAccessList.Should().NotBeNull();
        payloadResult.ExecutionPayload.BlockAccessList!.Length.Should().BeGreaterThan(0);
    }

    [TestCaseSource(nameof(BuildBlockV1ForkCases))]
    public async Task Testing_buildBlockV1_returns_fork_specific_payload(
        string targetFork,
        IReleaseSpec spec,
        bool expectsBlockAccessList,
        bool expectsSlotNumber)
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
            SlotNumber = expectsSlotNumber ? 1 : null
        };

        Block parentBlock = new(parentHeader, Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), Array.Empty<Withdrawal>());
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        IGasLimitCalculator gasLimitCalculator = Substitute.For<IGasLimitCalculator>();
        gasLimitCalculator.GetGasLimit(Arg.Any<BlockHeader>()).Returns(parentHeader.GasLimit);

        TestingRpcModule module = CreateTestingRpcModule(parentHash, parentBlock, specProvider, gasLimitCalculator);

        Transaction[] transactions = BuildSignedTransactions(2);
        byte[][] txRlps = EncodeTransactions(transactions, out string[] txHex);

        ulong? slotNumber = expectsSlotNumber ? parentHeader.SlotNumber!.Value + 1 : null;
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = parentHeader.Timestamp + 12,
            PrevRandao = Keccak.Compute("randao"),
            SuggestedFeeRecipient = Address.Zero,
            Withdrawals = [],
            ParentBeaconBlockRoot = Keccak.Compute("parentBeaconBlockRoot"),
            SlotNumber = slotNumber
        };

        string json = await RpcTest.TestSerializedRequest<ITestingRpcModule>(
            module,
            nameof(ITestingRpcModule.testing_buildBlockV1),
            parentHash,
            payloadAttributes,
            txRlps,
            Array.Empty<byte>(),
            targetFork);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement executionPayload = doc.RootElement.GetProperty("result").GetProperty("executionPayload");
        JsonElement transactionsJson = executionPayload.GetProperty("transactions");

        transactionsJson.GetArrayLength().Should().Be(txHex.Length);
        for (int i = 0; i < txHex.Length; i++)
        {
            transactionsJson[i].GetString().Should().Be(txHex[i]);
        }

        if (expectsBlockAccessList)
        {
            executionPayload.TryGetProperty("blockAccessList", out JsonElement blockAccessList).Should().BeTrue();
            blockAccessList.GetString().Should().NotBeNullOrEmpty();
        }
        else
        {
            executionPayload.TryGetProperty("blockAccessList", out _).Should().BeFalse();
        }

        if (expectsSlotNumber)
        {
            executionPayload.TryGetProperty("slotNumber", out JsonElement slotNumberJson).Should().BeTrue();
            slotNumberJson.GetString().Should().Be(slotNumber!.Value.ToHexString(skipLeadingZeros: true));
        }
        else
        {
            executionPayload.TryGetProperty("slotNumber", out _).Should().BeFalse();
        }
    }

    public static IEnumerable<TestCaseData> BuildBlockV1ForkCases()
    {
        yield return new TestCaseData("prague", Prague.Instance, false, false)
            .SetName("Testing_buildBlockV1_json_rpc_returns_payload_with_txs_for_prague");
        yield return new TestCaseData("osaka", Osaka.Instance, false, false)
            .SetName("Testing_buildBlockV1_json_rpc_returns_payload_with_txs_for_osaka");
        yield return new TestCaseData("amsterdam", Amsterdam.Instance, true, true)
            .SetName("Testing_buildBlockV1_json_rpc_returns_payload_with_txs_for_amsterdam");
    }

    private static TestingRpcModule CreateTestingRpcModule(
        Hash256 parentHash,
        Block parentBlock,
        ISpecProvider specProvider,
        IGasLimitCalculator gasLimitCalculator)
    {
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
                if (block.Header.SlotNumber is not null)
                {
                    block.BlockAccessList = new BlockAccessList();
                }
                return block;
            });

        IMainProcessingContext mainProcessingContext = Substitute.For<IMainProcessingContext>();
        mainProcessingContext.BlockchainProcessor.Returns(blockchainProcessor);

        return new TestingRpcModule(
            mainProcessingContext,
            gasLimitCalculator,
            specProvider,
            blockFinder,
            LimboLogs.Instance);
    }

    private static Transaction[] BuildSignedTransactions(int count)
    {
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            Transaction tx = CoreBuild.A.Transaction
                .WithNonce((UInt256)i)
                .WithTo(TestItem.AddressA)
                .WithChainId(TestBlockchainIds.ChainId)
                .WithType(TxType.EIP1559)
                .WithGasPrice(1.GWei())
                .WithMaxFeePerGas(1.GWei())
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;
            transactions[i] = tx;
        }

        return transactions;
    }

    private static byte[][] EncodeTransactions(Transaction[] transactions, out string[] txHex)
    {
        byte[][] encoded = new byte[transactions.Length][];
        txHex = new string[transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            byte[] rlp = Rlp.Encode(transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
            encoded[i] = rlp;
            txHex[i] = rlp.ToHexString(true);
        }

        return encoded;
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
