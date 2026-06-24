// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace Nethermind.Synchronization.Test;

/// <summary>
/// End to end sync test.
/// For each configuration, create a server with some spam transactions.
/// </summary>
/// <param name="dbMode"></param>
/// <param name="isPostMerge"></param>
[Parallelizable(ParallelScope.Children)]
[TestFixtureSource(nameof(CreateTestCases))]
public class E2ESyncTests(E2ESyncTests.DbMode dbMode, bool isPostMerge)
{
    public enum DbMode
    {
        Default,
        Hash,
        NoPruning,
        Flat
    }

    public static IEnumerable<TestFixtureParameters> CreateTestCases()
    {
        yield return new TestFixtureParameters(DbMode.Default, false);
        yield return new TestFixtureParameters(DbMode.Default, true);
        yield return new TestFixtureParameters(DbMode.Hash, false);
        yield return new TestFixtureParameters(DbMode.Hash, true);
        yield return new TestFixtureParameters(DbMode.NoPruning, false);
        yield return new TestFixtureParameters(DbMode.NoPruning, true);
        yield return new TestFixtureParameters(DbMode.Flat, false);
        yield return new TestFixtureParameters(DbMode.Flat, true);
    }

    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);
    private const int ChainLength = 1000;
    private const int HeadPivotDistance = 500;
    private static TimeSpan BalSyncTestTimeout = TimeSpan.FromMinutes(10);
    private const int BalSyncChainLength = 15_000;
    private const int PartialBalSyncChainLength = 1_000;
    private const int PartialBalActivationBlock = 400;
    private const int PartialBalSyncHeadPivotDistance = 500;
    private const int BalSyncBuildProgressInterval = 1_000;
    private const int BalSyncVerificationProgressInterval = 3_000;
    private static readonly DateTime PostMergeStartTime = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly ulong PostMergeStartTimestamp = (ulong)PostMergeStartTime.Subtract(DateTime.UnixEpoch).TotalSeconds;

    private static int _nextPortNumber = 30_000;
    private IContainer _server = null!;

    private int AllocatePort() =>
        Interlocked.Increment(ref _nextPortNumber);

    /// <summary>
    /// Replace all entries in a block-keyed dictionary with a single entry at block 0
    /// whose value is the sum of all original values. This preserves the cumulative effect
    /// while ensuring the dictionary keys don't inflate biggestBlockTransition.
    /// </summary>
    private static void RekeyDictionaryToGenesis(IDictionary<long, long>? dict)
    {
        if (dict is null or { Count: 0 }) return;
        long total = dict.Values.Sum();
        dict.Clear();
        dict[0] = total;
    }

    /// <summary>
    /// Replace all entries in a block reward dictionary with a single entry at block 0
    /// using the last (highest-block) reward value. This preserves the final block reward
    /// while ensuring the dictionary keys don't inflate biggestBlockTransition.
    /// </summary>
    private static void RekeyBlockRewardToGenesis(SortedDictionary<long, UInt256>? dict)
    {
        if (dict is null or { Count: 0 }) return;
        UInt256 lastReward = dict.Values.Last();
        dict.Clear();
        dict[0] = lastReward;
    }

    /// <summary>
    /// Activate all block-number-based forks from genesis so that biggestBlockTransition stays at 0,
    /// preventing the "Chainspec file is misconfigured" warning in short test chains.
    /// </summary>
    private static void ActivateAllBlockTransitionsFromGenesis(ChainSpec spec)
    {
        // ChainSpec block-number properties (collected by BuildTransitions via EndsWith("BlockNumber"))
        spec.HomesteadBlockNumber = 0;
        spec.DaoForkBlockNumber = null; // Disable DAO fork — it requires specific extra data in headers
        spec.TangerineWhistleBlockNumber = 0;
        spec.SpuriousDragonBlockNumber = 0;
        spec.ByzantiumBlockNumber = 0;
        // ConstantinopleBlockNumber is null on mainnet (eip1283DisableTransition not set) - keep null
        spec.ConstantinopleFixBlockNumber = 0;
        spec.IstanbulBlockNumber = 0;
        spec.BerlinBlockNumber = 0;
        spec.LondonBlockNumber = 0;
        spec.ArrowGlacierBlockNumber = 0;
        spec.GrayGlacierBlockNumber = 0;

        // ChainParameters block transitions (collected by BuildTransitions via EndsWith("Transition"))
        ActivateAllParameterTransitionsFromGenesis(spec.Parameters);

        // Ethash engine transitions and block-keyed dictionaries
        ActivateAllEthashTransitionsFromGenesis(spec);
    }

    private static void ActivateAllParameterTransitionsFromGenesis(ChainParameters parameters)
    {
        parameters.MaxCodeSizeTransition = 0;
        parameters.Eip150Transition = 0;
        parameters.Eip152Transition = 0;
        parameters.Eip160Transition = 0;
        parameters.Eip161abcTransition = 0;
        parameters.Eip161dTransition = 0;
        parameters.Eip155Transition = 0;
        parameters.Eip140Transition = 0;
        parameters.Eip211Transition = 0;
        parameters.Eip214Transition = 0;
        // Always on, as the timestamp based fork activation always override block number based
        // activation. However, the receipt message serializer does not check the block header of
        // the receipt for timestamp, only block number therefore it will always not encode with
        // Eip658, but the block builder always build with Eip658 as the latest fork activation
        // uses timestamp which is < than now.
        // TODO: Need to double check which code part does not pass in timestamp from header.
        parameters.Eip658Transition = 0;
        parameters.Eip145Transition = 0;
        parameters.Eip1014Transition = 0;
        parameters.Eip1052Transition = 0;
        parameters.Eip1108Transition = 0;
        parameters.Eip1344Transition = 0;
        parameters.Eip1884Transition = 0;
        parameters.Eip2028Transition = 0;
        parameters.Eip2200Transition = 0;
        parameters.Eip2565Transition = 0;
        parameters.Eip2929Transition = 0;
        parameters.Eip2930Transition = 0;
        parameters.Eip1559Transition = 0;
        parameters.Eip3198Transition = 0;
        parameters.Eip3529Transition = 0;
        parameters.Eip3541Transition = 0;
    }

    private static void ActivateAllEthashTransitionsFromGenesis(ChainSpec spec)
    {
        EthashChainSpecEngineParameters ethashParams = spec.EngineChainSpecParametersProvider.GetChainSpecParameters<EthashChainSpecEngineParameters>();
        ethashParams.HomesteadTransition = 0;
        ethashParams.DaoHardforkTransition = null; // Disable DAO fork — it requires specific extra data in headers
        ethashParams.Eip100bTransition = 0;
        // Re-key block-number-keyed dictionaries to block 0 so they don't inflate
        // biggestBlockTransition. Keep the values — clearing them breaks block rewards.
        RekeyDictionaryToGenesis(ethashParams.DifficultyBombDelays);
        RekeyBlockRewardToGenesis(ethashParams.BlockReward);
    }

    /// <summary>
    /// Common code for all node
    /// </summary>
    private Task<IContainer> CreateNode(PrivateKey nodeKey, Func<IConfigProvider, ChainSpec, Task> configurer, PrivateKey? fundedAccountKey = null) =>
        CreateNode(nodeKey, configurer, dbMode, fundedAccountKey);

    private async Task<IContainer> CreateNode(
        PrivateKey nodeKey,
        Func<IConfigProvider, ChainSpec, Task> configurer,
        DbMode dbModeOverride,
        PrivateKey? fundedAccountKey = null)
    {
        IConfigProvider configProvider = new ConfigProvider();
        ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile("chainspec/foundation.json");

        // Set basefeepergas in genesis or it will fail 1559 validation.
        spec.Genesis.Header.BaseFeePerGas = 10.Wei;

        // Needed for generating spam state.
        spec.Genesis.Header.GasLimit = 1_000_000_000;
        spec.Allocations[(fundedAccountKey ?? TestItem.PrivateKeyA).Address] = new ChainSpecAllocation(300.Ether);

        spec.Allocations[Eip7002Constants.WithdrawalRequestPredeployAddress] = new ChainSpecAllocation
        {
            Code = Eip7002TestConstants.Code,
            Nonce = Eip7002TestConstants.Nonce
        };

        spec.Allocations[Eip7251Constants.ConsolidationRequestPredeployAddress] = new ChainSpecAllocation
        {
            Code = Eip7251TestConstants.Code,
            Nonce = Eip7251TestConstants.Nonce
        };

        // Activate all block-number-based forks from genesis. The test builds a short chain
        // (1000 blocks) with post-merge timestamps. Without this, the chainspec has block
        // transitions at high mainnet block numbers (e.g. London at 12,965,000), causing a
        // legitimate "Chainspec file is misconfigured" warning when GetSpec is called with
        // low block numbers but high timestamps.
        ActivateAllBlockTransitionsFromGenesis(spec);

        if (isPostMerge)
        {
            spec.Genesis.Header.Difficulty = 10000;

            IMergeConfig mergeConfig = configProvider.GetConfig<IMergeConfig>();
            mergeConfig.Enabled = true;
            mergeConfig.TerminalTotalDifficulty = "10000";
            mergeConfig.FinalTotalDifficulty = "10000";
        }

        await configurer(configProvider, spec);

        switch (dbModeOverride)
        {
            case DbMode.Default:
                // Um... nothing?
                break;
            case DbMode.Hash:
                {
                    IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
                    initConfig.StateDbKeyScheme = INodeStorage.KeyScheme.Hash;
                    break;
                }
            case DbMode.NoPruning:
                {
                    IPruningConfig pruningConfig = configProvider.GetConfig<IPruningConfig>();
                    pruningConfig.Mode = PruningMode.None;
                    break;
                }
            case DbMode.Flat:
                {
                    IFlatDbConfig flatDbConfig = configProvider.GetConfig<IFlatDbConfig>();
                    flatDbConfig.Enabled = true;
                    flatDbConfig.VerifyWithTrie = true;
                    break;
                }
        }

        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(spec, configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(nodeKey, $"{nameof(E2ESyncTests)} {dbMode} {isPostMerge}"))
            .AddSingleton<IDisconnectsAnalyzer, ImmediateDisconnectFailure>()
            .AddSingleton<SyncTestContext>()
            .AddSingleton<ITestEnv, PreMergeTestEnv>()
            .AddSingleton<BlockProcessorExceptionDetector>()
            .AddSingleton<ILogManager>(new TestLogManager(LogLevel.Info)) // Put last or it wont work.
            .AddDecorator<IBlockProcessor, BlockProcessorExceptionDetector.BlockProcessorInterceptor>();

        if (isPostMerge)
        {
            // Activate configured mainnet future EIP
            ManualTimestamper timestamper = new(PostMergeStartTime);
            builder
                .AddModule(new TestMergeModule(configProvider.GetConfig<ITxPoolConfig>()))
                .AddSingleton<ManualTimestamper>(timestamper) // Used by test code
                .AddDecorator<ITestEnv, PostMergeTestEnv>()
                ;
        }
        else
        {
            // So that any EIP after the merge is not activated.
            ManualTimestamper timestamper = ManualTimestamper.PreMerge;
            builder
                .AddSingleton<ManualTimestamper>(timestamper) // Used by test code
                .AddSingleton<ITimestamper>(timestamper)
                ;
        }

        IContainer container = builder.Build();

        if (isPostMerge)
        {
            EnablePostMergeEthCapabilities(container.Resolve<IProtocolsManager>());
        }

        return container;
    }

    private static void EnablePostMergeEthCapabilities(IProtocolsManager protocolsManager)
    {
        ArgumentNullException.ThrowIfNull(protocolsManager);
        protocolsManager.AddSupportedCapability(new Capability(Protocol.Eth, EthVersions.Eth69));
        protocolsManager.AddSupportedCapability(new Capability(Protocol.Eth, EthVersions.Eth70));
        protocolsManager.AddSupportedCapability(new Capability(Protocol.Eth, EthVersions.Eth71));
    }

    private static void EnableBlockAccessListsFromGenesis(ChainSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        MoveBlockTransitionsToGenesis(spec);
        spec.Parameters.Eip7928TransitionTimestamp = spec.Genesis.Header.Timestamp;
        spec.Genesis.Header.BlockAccessListHash = Keccak.OfAnEmptySequenceRlp;
    }

    private static void EnableBlockAccessListsAtBlock(ChainSpec spec, ulong activationBlockNumber)
    {
        ArgumentNullException.ThrowIfNull(spec);
        MoveBlockTransitionsToGenesis(spec);
        spec.Parameters.Eip7928TransitionTimestamp = PostMergeStartTimestamp + activationBlockNumber;
        spec.Genesis.Header.BlockAccessListHash = null;
    }

    private static void MoveBlockTransitionsToGenesis(ChainSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        SetNumericTransitionsToGenesis(spec);
        SetNumericTransitionsToGenesis(spec.Parameters);
        NormalizeEngineBlockTransitions(spec.EngineChainSpecParametersProvider);
    }

    private static void NormalizeEngineBlockTransitions(IChainSpecParametersProvider engineChainSpecParametersProvider)
    {
        ArgumentNullException.ThrowIfNull(engineChainSpecParametersProvider);

        foreach (IChainSpecEngineParameters engineParameters in engineChainSpecParametersProvider.AllChainSpecParameters)
        {
            SetNumericTransitionsToGenesis(engineParameters);

            if (engineParameters is EthashChainSpecEngineParameters ethashParameters)
            {
                ethashParameters.BlockReward = [];
                ethashParameters.DifficultyBombDelays = new Dictionary<long, long>();
            }
        }
    }

    private static void SetNumericTransitionsToGenesis(object target)
    {
        ArgumentNullException.ThrowIfNull(target);

        PropertyInfo[] properties = target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        foreach (PropertyInfo property in properties)
        {
            bool isTimestampTransition =
                property.Name.EndsWith("Timestamp", StringComparison.Ordinal) ||
                property.Name.EndsWith("TransitionTimestamp", StringComparison.Ordinal);

            if (isTimestampTransition)
            {
                continue;
            }

            bool isNumericTransition =
                property.Name.EndsWith("Transition", StringComparison.Ordinal) ||
                property.Name.EndsWith("BlockNumber", StringComparison.Ordinal);

            if (!isNumericTransition)
            {
                continue;
            }

            if (property.PropertyType == typeof(long))
            {
                property.SetValue(target, 0L);
            }
            else if (property.PropertyType == typeof(long?) && property.GetValue(target) is not null)
            {
                property.SetValue(target, 0L);
            }
        }
    }

    private static void ConfigureLocalNetwork(IConfigProvider configProvider, int port)
    {
        INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
        networkConfig.P2PPort = port;
        // Disable IP filtering for E2E tests as all nodes run on localhost
        networkConfig.FilterPeersByRecentIp = false;
        networkConfig.FilterDiscoveryNodesByRecentIp = false;
    }

    private async Task StartServerAndBuildStorageChain(
        IContainer server,
        int chainLength,
        CancellationToken cancellationToken,
        string progressLabel)
    {
        SyncTestContext serverCtx = server.Resolve<SyncTestContext>();
        await serverCtx.StartBlockProcessing(cancellationToken);

        TestContext.Progress.WriteLine($"{progressLabel}: building {chainLength} storage blocks.");
        for (int i = 0; i < chainLength; i++)
        {
            await serverCtx.BuildBlockWithStorage(i, cancellationToken);

            if ((i + 1) % BalSyncBuildProgressInterval == 0 || i == chainLength - 1)
            {
                TestContext.Progress.WriteLine($"{progressLabel}: built {i + 1}/{chainLength} blocks.");
            }
        }

        await serverCtx.StartNetwork(cancellationToken);
    }

    [OneTimeSetUp]
    public async Task SetupServer()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.CancelAfter(SetupTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        PrivateKey serverKey = TestItem.PrivateKeyA;
        _server = await CreateNode(serverKey, (cfg, spec) =>
        {
            ConfigureLocalNetwork(cfg, AllocatePort());
            return Task.CompletedTask;
        });

        await StartServerAndBuildStorageChain(_server, ChainLength, cancellationToken, "Setup server");
    }

    [OneTimeTearDown]
    public Task TearDownServer() =>
        _server.DisposeAsync().AsTask();

    [Test]
    [Category("Flaky"), Retry(5)]
    public async Task FullSync()
    {
        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);

        PrivateKey clientKey = TestItem.PrivateKeyB;
        await using IContainer client = await CreateNode(clientKey, (cfg, spec) =>
        {
            ConfigureLocalNetwork(cfg, AllocatePort());
            return Task.CompletedTask;
        });

        await client.Resolve<SyncTestContext>().SyncFromServer(_server, cancellationTokenSource.Token);
    }

    [Test]
    [Category("Flaky"), Retry(5)]
    public async Task FastSync()
    {
        // After the nodedata satellite protocol was removed, fast sync without snap can no longer
        // retrieve state on eth >= 67 (no GetNodeData in those versions). The SnapSync test below
        // covers fast sync with state retrieval via snap.
        Assert.Ignore("Fast sync without snap is not supported for eth >= 67 after nodedata satellite removal");

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);

        PrivateKey clientKey = TestItem.PrivateKeyC;
        await using IContainer client = await CreateNode(clientKey, async (cfg, spec) =>
        {
            SyncConfig syncConfig = (SyncConfig)cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;

            await SetPivot(syncConfig, cancellationTokenSource.Token);

            ConfigureLocalNetwork(cfg, AllocatePort());
        });

        await client.Resolve<SyncTestContext>().SyncFromServer(_server, cancellationTokenSource.Token);
    }

    private async Task SetPivot(SyncConfig syncConfig, CancellationToken cancellationToken) =>
        await SetPivot(_server, syncConfig, cancellationToken, HeadPivotDistance);

    private static async Task SetPivot(IContainer server, SyncConfig syncConfig, CancellationToken cancellationToken, int headPivotDistance)
    {
        IBlockProcessingQueue blockProcessingQueue = server.Resolve<IBlockProcessingQueue>();
        await blockProcessingQueue.WaitForBlockProcessing(cancellationToken);
        IBlockTree serverBlockTree = server.Resolve<IBlockTree>();
        long serverHeadNumber = serverBlockTree.Head!.Number;
        BlockHeader pivot = serverBlockTree.FindHeader(serverHeadNumber - headPivotDistance)!;
        syncConfig.PivotHash = pivot.Hash!.ToString();
        syncConfig.PivotNumber = pivot.Number;
        syncConfig.PivotTotalDifficulty = pivot.TotalDifficulty!.Value.ToString();
    }

    [Test]
    [Category("Flaky"), Retry(5)]
    public async Task SnapSync()
    {
        if (dbMode == DbMode.Hash) Assert.Ignore("Hash db does not support snap sync");

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);
        await RunSnapSyncOnce(cancellationTokenSource.Token);
    }

    // Stress reproducer for SnapSync Windows flake — run manually; see PR #11443 for context.
    [Test, Explicit("Stress reproducer for SnapSync Windows flake — run manually")]
    [TestCaseSource(nameof(StressIterations))]
    public async Task SnapSync_StressRepro(int iteration)
    {
        if (dbMode != DbMode.Flat) Assert.Ignore("Stress repro only targets the Flat dbMode where the flake was observed");
        _ = iteration; // index is purely to give NUnit a unique case per attempt

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);
        await RunSnapSyncOnce(cancellationTokenSource.Token);
    }

    private async Task RunSnapSyncOnce(CancellationToken cancellationToken)
    {
        PrivateKey clientKey = TestItem.PrivateKeyD;
        await using IContainer client = await CreateNode(clientKey, async (cfg, spec) =>
        {
            SyncConfig syncConfig = (SyncConfig)cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;
            syncConfig.SnapSync = true;

            await SetPivot(syncConfig, cancellationToken);

            ConfigureLocalNetwork(cfg, AllocatePort());
        });

        await client.Resolve<SyncTestContext>().SyncFromServer(_server, cancellationToken);
    }

    private const int StressIterationCount = 30;
    private static IEnumerable<int> StressIterations() => Enumerable.Range(0, StressIterationCount);

    [Test]
    [Category("Flaky"), Retry(2)]
    public async Task FastSync_downloads_block_access_lists_over_eth71()
    {
        if (!isPostMerge || dbMode != DbMode.Default)
        {
            Assert.Ignore("BAL sync regression is only executed for the default post-merge fixture.");
        }

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(BalSyncTestTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        PrivateKey serverKey = TestItem.PrivateKeyE;
        await using IContainer server = await CreateNode(serverKey, (cfg, spec) =>
        {
            EnableBlockAccessListsFromGenesis(spec);
            ConfigureLocalNetwork(cfg, AllocatePort());
            return Task.CompletedTask;
        }, serverKey);

        await StartServerAndBuildStorageChain(server, BalSyncChainLength, cancellationToken, "BAL sync server");

        IBlockTree serverBlockTree = server.Resolve<IBlockTree>();
        Assert.That(serverBlockTree.Head!.Number, Is.EqualTo(BalSyncChainLength));

        IBlockAccessListStore serverBalStore = server.Resolve<IBlockAccessListStore>();
        using (MemoryManager<byte>? serverBal = serverBalStore.GetRlp(1, serverBlockTree.FindBlock(1)!.Hash!))
        {
            Assert.That(serverBal, Is.Not.Null);
        }

        long syncPivotNumber = 0;
        PrivateKey clientKey = TestItem.PrivateKeyF;
        await using IContainer client = await CreateNode(clientKey, async (cfg, spec) =>
        {
            EnableBlockAccessListsFromGenesis(spec);

            SyncConfig syncConfig = (SyncConfig)cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;

            await SetPivot(server, syncConfig, cancellationToken, HeadPivotDistance);
            syncPivotNumber = syncConfig.PivotNumber;

            ConfigureLocalNetwork(cfg, AllocatePort());
        }, serverKey);

        Assert.That(syncPivotNumber, Is.GreaterThan(1));
        TestContext.Progress.WriteLine($"BAL sync test: head {BalSyncChainLength}, pivot {syncPivotNumber}.");

        await client.Resolve<SyncTestContext>().SyncFromServerAndVerifyAccessLists(server, syncPivotNumber, cancellationToken);
        Assert.That(client.Resolve<ISyncPointers>().LowestInsertedBlockAccessListBlockNumber, Is.LessThanOrEqualTo(1));
    }

    [Test]
    [Category("Flaky"), Retry(5)]
    public async Task SnapSync_HalfPathServer_HashClient()
    {
        if (dbMode != DbMode.Default) Assert.Ignore("This test only runs on the Default (HalfPath) server fixture");

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);

        PrivateKey clientKey = TestItem.PrivateKeyD;
        await using IContainer client = await CreateNode(clientKey, async (cfg, spec) =>
        {
            SyncConfig syncConfig = (SyncConfig)cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;
            syncConfig.SnapSync = true;

            await SetPivot(syncConfig, cancellationTokenSource.Token);

            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = AllocatePort();
            networkConfig.FilterPeersByRecentIp = false;
            networkConfig.FilterDiscoveryNodesByRecentIp = false;
        }, DbMode.Hash);

        await client.Resolve<SyncTestContext>().SyncFromServer(_server, cancellationTokenSource.Token);
    }

    [Test]
    [Category("Flaky"), Retry(2)]
    public async Task FastSync_skips_pre_eip7928_block_access_lists_over_eth71()
    {
        if (!isPostMerge || dbMode != DbMode.Default)
        {
            Assert.Ignore("BAL sync regression is only executed for the default post-merge fixture.");
        }

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(BalSyncTestTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        PrivateKey serverKey = TestItem.PrivateKeyE;
        await using IContainer server = await CreateNode(serverKey, (cfg, spec) =>
        {
            EnableBlockAccessListsAtBlock(spec, PartialBalActivationBlock);
            ConfigureLocalNetwork(cfg, AllocatePort());
            return Task.CompletedTask;
        }, serverKey);

        await StartServerAndBuildStorageChain(server, PartialBalSyncChainLength, cancellationToken, "Partial BAL sync server");

        IBlockTree serverBlockTree = server.Resolve<IBlockTree>();
        Assert.That(serverBlockTree.Head!.Number, Is.EqualTo(PartialBalSyncChainLength));

        IBlockAccessListStore serverBalStore = server.Resolve<IBlockAccessListStore>();
        Block lastPreActivationBlock = serverBlockTree.FindBlock(PartialBalActivationBlock - 1)!;
        Block firstActivatedBlock = serverBlockTree.FindBlock(PartialBalActivationBlock)!;
        Assert.That(lastPreActivationBlock.Header.BlockAccessListHash, Is.Null);
        using (MemoryManager<byte>? preActivationBal = serverBalStore.GetRlp(lastPreActivationBlock.Number, lastPreActivationBlock.Hash!))
        {
            Assert.That(preActivationBal, Is.Null);
        }
        Assert.That(firstActivatedBlock.Header.BlockAccessListHash, Is.Not.Null);
        using (MemoryManager<byte>? firstActivatedBal = serverBalStore.GetRlp(firstActivatedBlock.Number, firstActivatedBlock.Hash!))
        {
            Assert.That(firstActivatedBal, Is.Not.Null);
        }

        long syncPivotNumber = 0;
        PrivateKey clientKey = TestItem.PrivateKeyF;
        await using IContainer client = await CreateNode(clientKey, async (cfg, spec) =>
        {
            EnableBlockAccessListsAtBlock(spec, PartialBalActivationBlock);

            SyncConfig syncConfig = (SyncConfig)cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;

            await SetPivot(server, syncConfig, cancellationToken, PartialBalSyncHeadPivotDistance);
            syncPivotNumber = syncConfig.PivotNumber;

            ConfigureLocalNetwork(cfg, AllocatePort());
        }, serverKey);

        Assert.That(syncPivotNumber, Is.GreaterThan(PartialBalActivationBlock));
        TestContext.Progress.WriteLine($"Partial BAL sync test: head {PartialBalSyncChainLength}, pivot {syncPivotNumber}, activation {PartialBalActivationBlock}.");

        await client.Resolve<SyncTestContext>().SyncFromServerAndVerifyAccessLists(server, syncPivotNumber, cancellationToken);
        Assert.That(client.Resolve<ISyncPointers>().LowestInsertedBlockAccessListBlockNumber, Is.LessThanOrEqualTo(1));
    }

    // Post and pre merge have slightly different operation for these.
    private interface ITestEnv
    {
        Task BuildBlockWithTxs(Transaction[] transactions, CancellationToken cancellation);
        Task SyncUntilFinished(IContainer server, CancellationToken cancellationToken, long finalizedDistanceFromHead);
        Task WaitForSyncMode(Func<SyncMode, bool> modeCheck, CancellationToken cancellationToken);
    }

    private class PreMergeTestEnv(
        IBlockTree blockTree,
        ITxPool txPool,
        ManualTimestamper timestamper,
        IManualBlockProductionTrigger manualBlockProductionTrigger,
        ISyncModeSelector syncModeSelector
    ) : ITestEnv
    {
        public virtual async Task BuildBlockWithTxs(Transaction[] transactions, CancellationToken cancellation)
        {
            Task newBlockTask = blockTree.WaitForNewBlock(cancellation);

            AcceptTxResult[] txResults = transactions.Select(t => txPool.SubmitTx(t, TxHandlingOptions.None)).ToArray();
            foreach (AcceptTxResult acceptTxResult in txResults)
            {
                Assert.That(acceptTxResult, Is.EqualTo(AcceptTxResult.Accepted));
            }

            timestamper.Add(TimeSpan.FromSeconds(1));
            try
            {
                Assert.That(await manualBlockProductionTrigger.BuildBlock(), Is.Not.Null);
                await newBlockTask;
            }
            catch (Exception e)
            {
                Assert.Fail($"Error building block. Head: {blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}, {e}");
            }
        }


        public virtual async Task SyncUntilFinished(IContainer server, CancellationToken cancellationToken, long finalizedDistanceFromHead)
        {
            await WaitForSyncMode(mode => (mode == SyncMode.WaitingForBlock || mode == SyncMode.None || mode == SyncMode.Full), cancellationToken);

            // Wait until head match
            BlockHeader serverHead = server.Resolve<IBlockTree>().Head?.Header!;
            if (blockTree.Head?.Number == serverHead?.Number) return;
            await Wait.ForEventCondition<BlockReplacementEventArgs>(
                cancellationToken,
                (h) => blockTree.BlockAddedToMain += h,
                (h) => blockTree.BlockAddedToMain -= h,
                (e) => e.Block.Number == serverHead?.Number);
        }

        public async Task WaitForSyncMode(Func<SyncMode, bool> modeCheck, CancellationToken cancellationToken)
        {
            if (modeCheck(syncModeSelector.Current)) return;
            await Wait.ForEventCondition<SyncModeChangedEventArgs>(cancellationToken,
                h => syncModeSelector.Changed += h,
                h => syncModeSelector.Changed -= h,
                (e) => modeCheck(e.Current));
        }
    }

    private class PostMergeTestEnv(
        IBlockTree blockTree,
        ITxPool txPool,
        ManualTimestamper timestamper,
        IPayloadPreparationService payloadPreparationService,
        IBlockCacheService blockCacheService,
        IMergeSyncController mergeSyncController,
        ISyncConfig syncConfig,
        ITestEnv preMergeTestEnv
    ) : ITestEnv
    {
        public async Task BuildBlockWithTxs(Transaction[] transactions, CancellationToken cancellation)
        {
            Task newBlockTask = blockTree.WaitForNewBlock(cancellation);

            AcceptTxResult[] txResults = transactions.Select(t => txPool.SubmitTx(t, TxHandlingOptions.None)).ToArray();
            foreach (AcceptTxResult acceptTxResult in txResults)
            {
                Assert.That(acceptTxResult, Is.EqualTo(AcceptTxResult.Accepted));
            }
            timestamper.Add(TimeSpan.FromSeconds(1));

            string? payloadId = payloadPreparationService.StartPreparingPayload(blockTree.Head?.Header!, new PayloadAttributes()
            {
                PrevRandao = Hash256.Zero,
                SuggestedFeeRecipient = TestItem.AddressA,
                Withdrawals = [],
                ParentBeaconBlockRoot = Hash256.Zero,
                Timestamp = (ulong)timestamper.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds
            });
            Assert.That(payloadId, Is.Not.Null.And.Not.Empty);

            IBlockProductionContext? blockProductionContext = await payloadPreparationService.GetPayload(payloadId!, skipCancel: true);
            Assert.That(blockProductionContext, Is.Not.Null);
            Assert.That(blockProductionContext!.CurrentBestBlock, Is.Not.Null);

            Assert.That(await blockTree.SuggestBlockAsync(blockProductionContext.CurrentBestBlock!), Is.EqualTo(AddBlockResult.Added));

            await newBlockTask;
        }

        public async Task SyncUntilFinished(IContainer server, CancellationToken cancellationToken, long finalizedDistanceFromHead)
        {
            IBlockTree otherBlockTree = server.Resolve<IBlockTree>();
            long finalizedBlockNumber = Math.Max(0, otherBlockTree.Head!.Number - finalizedDistanceFromHead);
            Block finalizedBlock = otherBlockTree.FindBlock(finalizedBlockNumber)!;
            Block headBlock = otherBlockTree.Head!;
            blockCacheService.TryAddBlock(finalizedBlock);
            blockCacheService.TryAddBlock(headBlock);
            blockCacheService.FinalizedHash = finalizedBlock.Hash!;

            // In fast sync the starting pivot is resolved from the finalized block (before the sync mode
            // selector starts); wait for that before kicking off beacon header sync. Full sync keeps the
            // config pivot and never resolves a fresh one, so there is nothing to wait for.
            if (syncConfig.FastSync)
            {
                while (blockTree.SyncPivot.BlockHash != finalizedBlock.Hash)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }
            mergeSyncController.InitBeaconHeaderSync(headBlock.Header);

            await preMergeTestEnv.SyncUntilFinished(server, cancellationToken, finalizedDistanceFromHead);
        }

        public Task WaitForSyncMode(Func<SyncMode, bool> modeCheck, CancellationToken cancellationToken) =>
            preMergeTestEnv.WaitForSyncMode(modeCheck, cancellationToken);
    }

    private class SyncTestContext(
        [KeyFilter(TestEnvironmentModule.NodeKey)] PrivateKey nodeKey,
        ISpecProvider specProvider,
        IEthereumEcdsa ecdsa,
        IBlockTree blockTree,
        IBlockAccessListStore blockAccessListStore,
        IReceiptStorage receiptStorage,
        IBlockProcessingQueue blockProcessingQueue,
        ITestEnv testEnv,
        IRlpxHost rlpxHost,
        IWorldStateManager worldStateManager,
        PseudoNethermindRunner runner,
        ImmediateDisconnectFailure immediateDisconnectFailure,
        BlockProcessorExceptionDetector blockProcessorExceptionDetector)
    {
        // These check is really slow (it doubles the test time) so its disabled by default.
        private const bool CheckBlocksAndReceiptsContent = false;
        private const bool VerifyTrieOnFinished = false;
        private const int DeployEveryNBlocks = 10;

        private readonly BlockDecoder _blockDecoder = new();
        private readonly ReceiptsMessageSerializer _receiptsMessageSerializer = new(specProvider);

        // Track deployed contracts for storage testing
        private readonly List<Address> _deployedContracts = [];
        private readonly Random _random = new(42); // Fixed seed for reproducibility

        // Runtime code: SLOAD slot 0, ADD 1, SSTORE to slot 0
        private readonly byte[] _runtimeCode = Prepare.EvmCode
            .PushData(0)              // slot 0
            .Op(Instruction.SLOAD)    // load current value
            .PushData(1)              // value to add
            .Op(Instruction.ADD)      // add 1
            .PushData(0)              // slot 0
            .Op(Instruction.SSTORE)   // store incremented value
            .Op(Instruction.STOP)
            .Done;

        // Initcode: set initial value in slot 0, then return runtime code
        private byte[]? _initCode;
        private byte[] InitCode => _initCode ??= Prepare.EvmCode
            .PushData(1)              // initial value
            .PushData(0)              // slot 0
            .Op(Instruction.SSTORE)   // set initial storage
            .ForInitOf(_runtimeCode)  // return runtime code
            .Done;

        public Task StartBlockProcessing(CancellationToken cancellationToken) =>
            runner.StartBlockProcessing(cancellationToken);

        public Task StartNetwork(CancellationToken cancellationToken) =>
            runner.StartNetwork(cancellationToken);

        private async Task ConnectTo(IContainer server, CancellationToken cancellationToken)
        {
            IEnode serverEnode = server.Resolve<IEnode>();
            Node serverNode = new(serverEnode.PublicKey, new IPEndPoint(serverEnode.HostIp, serverEnode.Port));
            if (!await rlpxHost.ConnectAsync(serverNode))
            {
                throw new NetworkingException($"Failed to connect to {serverNode:s}", NetworkExceptionType.TargetUnreachable);
            }
        }

        private readonly Dictionary<Address, UInt256> _nonces = [];

        public async Task BuildBlockWithCode(byte[][] codes, CancellationToken cancellation)
        {
            // 1 000 000 000
            long gasLimit = 1_000_000;

            _nonces.TryGetValue(nodeKey.Address, out UInt256 currentNonce);
            IReleaseSpec spec = specProvider.GetSpec((blockTree.Head?.Number) + 1 ?? 0, null);
            Transaction[] txs = codes.Select((byteCode) => Build.A.Transaction
                    .WithCode(byteCode)
                    .WithNonce(currentNonce++)
                    .WithGasLimit(gasLimit)
                    .WithGasPrice(10.GWei)
                    .SignedAndResolved(ecdsa, nodeKey, spec.IsEip155Enabled).TestObject)
                .ToArray();
            _nonces[nodeKey.Address] = currentNonce;
            await testEnv.BuildBlockWithTxs(txs, cancellation);
        }

        public async Task BuildBlockWithStorage(int blockNumber, CancellationToken cancellation)
        {
            long gasLimit = 200_000;

            _nonces.TryGetValue(nodeKey.Address, out UInt256 currentNonce);
            IReleaseSpec spec = specProvider.GetSpec((blockTree.Head?.Number ?? 0) + 1, null);

            Transaction tx;

            if (blockNumber % DeployEveryNBlocks == 0 || _deployedContracts.Count == 0)
            {
                // Deploy new contract
                tx = Build.A.Transaction
                    .WithCode(InitCode)
                    .WithNonce(currentNonce++)
                    .WithGasLimit(gasLimit)
                    .WithGasPrice(10.GWei)
                    .SignedAndResolved(ecdsa, nodeKey, spec.IsEip155Enabled).TestObject;

                // Calculate deployed address and track it
                Address deployedAddress = ContractAddress.From(nodeKey.Address, currentNonce - 1);
                _deployedContracts.Add(deployedAddress);
            }
            else
            {
                // Call random existing contract
                Address target = _deployedContracts[_random.Next(_deployedContracts.Count)];
                tx = Build.A.Transaction
                    .WithTo(target)
                    .WithData([])
                    .WithNonce(currentNonce++)
                    .WithGasLimit(gasLimit)
                    .WithGasPrice(10.GWei)
                    .SignedAndResolved(ecdsa, nodeKey, spec.IsEip155Enabled).TestObject;
            }

            _nonces[nodeKey.Address] = currentNonce;
            await testEnv.BuildBlockWithTxs([tx], cancellation);
        }

        private async Task VerifyHeadWith(IContainer server, CancellationToken cancellationToken)
        {
            await blockProcessingQueue.WaitForBlockProcessing(cancellationToken);

            IBlockTree otherBlockTree = server.Resolve<IBlockTree>();

            AssertBlockEqual(blockTree.Head!, otherBlockTree.Head!);

            if (VerifyTrieOnFinished)
#pragma warning disable CS0162 // Unreachable code detected
            {
                IWorldStateManager worldStateManager = server.Resolve<IWorldStateManager>();
                Assert.That(worldStateManager.VerifyTrie(blockTree.Head!.Header, cancellationToken), Is.True);
            }
#pragma warning restore CS0162 // Unreachable code detected
        }

        private ValueTask VerifyAllBlocksAndReceipts(IContainer server, CancellationToken cancellationToken)
        {
            IBlockTree otherBlockTree = server.Resolve<IBlockTree>();
            IReceiptStorage otherReceiptStorage = server.Resolve<IReceiptStorage>();

            for (int i = 0; i < otherBlockTree.Head?.Number; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Block clientBlock = blockTree.FindBlock(i)!;
                TxReceipt[] clientReceipts = receiptStorage.Get(clientBlock);
                Assert.That(clientBlock, Is.Not.Null);
                Assert.That(clientReceipts, Is.Not.Null);

                if (CheckBlocksAndReceiptsContent)
#pragma warning disable CS0162 // Unreachable code detected
                {
                    Block serverBlock = otherBlockTree.FindBlock(i)!;
                    AssertBlockEqual(clientBlock, serverBlock);
                    TxReceipt[] serverReceipts = otherReceiptStorage.Get(serverBlock);
                    AssertReceiptsEqual(clientReceipts, serverReceipts);
                }
#pragma warning restore CS0162 // Unreachable code detected
            }

            return ValueTask.CompletedTask;
        }

        private void AssertBlockEqual(Block block1, Block block2)
        {
            using ArrayPoolSpan<byte> stream1 = _blockDecoder.EncodeToArrayPoolSpan(block1);
            using ArrayPoolSpan<byte> stream2 = _blockDecoder.EncodeToArrayPoolSpan(block2);

            Assert.That(((ReadOnlySpan<byte>)stream1).ToArray(), Is.EqualTo(((ReadOnlySpan<byte>)stream2).ToArray()));
        }

        private void AssertReceiptsEqual(TxReceipt[] receipts1, TxReceipt[] receipts2) =>
            // The network encoding is not the same as storage encoding.
            Assert.That(EncodeReceipts(receipts1), Is.EqualTo(EncodeReceipts(receipts2)));

        private byte[] EncodeReceipts(TxReceipt[] receipts)
        {
            TxReceipt[][] wrappedReceipts = new[] { receipts };
            using ReceiptsMessage asReceiptsMessage = new(wrappedReceipts.ToPooledList());

            IByteBuffer bb = PooledByteBufferAllocator.Default.Buffer(1024);
            try
            {
                _receiptsMessageSerializer.Serialize(bb, asReceiptsMessage);
                return bb.AsSpan().ToArray();
            }
            finally
            {
                bb.Release();
            }
        }

        public async Task SyncFromServer(IContainer server, CancellationToken cancellationToken) =>
            await ExecuteSyncFromServer(server, async (sourceServer, token) =>
            {
                await VerifyHeadWith(sourceServer, token);
                await VerifyAllBlocksAndReceipts(sourceServer, token);
            }, cancellationToken);

        public async Task SyncFromServerAndVerifyAccessLists(IContainer server, long syncPivotNumber, CancellationToken cancellationToken) =>
            await ExecuteSyncFromServer(server, async (sourceServer, token) =>
            {
                await VerifyHeadWith(sourceServer, token);
                await VerifyBlockAccessListsWith(sourceServer, syncPivotNumber, token);
            }, cancellationToken);

        private async Task ExecuteSyncFromServer(
            IContainer server,
            Func<IContainer, CancellationToken, Task> verification,
            CancellationToken cancellationToken,
            long finalizedDistanceFromHead = 250)
        {
            await immediateDisconnectFailure.WatchForDisconnection(async (token) =>
            {
                await blockProcessorExceptionDetector.WatchForFailure(async (token) =>
                {
                    await runner.StartNetwork(token);
                    await ConnectTo(server, token);
                    await testEnv.SyncUntilFinished(server, token, finalizedDistanceFromHead);
                    await verification(server, token);
                }, token);
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // On flat, verify trie only work with persistence
            worldStateManager.FlushCache(cancellationToken);

            BlockHeader? head = blockTree.Head?.Header;
            Console.Error.WriteLine($"On {head?.ToString(BlockHeader.Format.Short)}");
            bool stateVerified = worldStateManager.VerifyTrie(head!, cancellationToken);
            Assert.That(stateVerified, Is.True);
        }

        private Task VerifyBlockAccessListsWith(IContainer server, long syncPivotNumber, CancellationToken cancellationToken)
        {
            IBlockTree sourceBlockTree = server.Resolve<IBlockTree>();
            IBlockAccessListStore sourceBlockAccessListStore = server.Resolve<IBlockAccessListStore>();
            long sourceHeadNumber = sourceBlockTree.Head!.Number;

            TestContext.Progress.WriteLine($"BAL sync verification: comparing BAL presence and exact BAL blobs for blocks 1-{sourceHeadNumber}. Pivot {syncPivotNumber}.");
            for (long blockNumber = 1; blockNumber <= sourceHeadNumber; blockNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CompareBlockAccessList(blockNumber);

                if (blockNumber % BalSyncVerificationProgressInterval == 0 || blockNumber == sourceHeadNumber)
                {
                    TestContext.Progress.WriteLine($"BAL sync verification: compared {blockNumber}/{sourceHeadNumber} BALs.");
                }
            }

            TestContext.Progress.WriteLine($"BAL sync verification: BALs matched through head {sourceHeadNumber}.");
            return Task.CompletedTask;

            void CompareBlockAccessList(long blockNumber)
            {
                Block sourceBlock = sourceBlockTree.FindBlock(blockNumber)!;
                Block syncedBlock = blockTree.FindBlock(blockNumber)!;

                byte[]? sourceBal = GetBlockAccessListRlp(sourceBlockAccessListStore, sourceBlock.Number, sourceBlock.Hash!);
                byte[]? syncedBal = GetBlockAccessListRlp(blockAccessListStore, syncedBlock.Number, syncedBlock.Hash!);
                bool balEnabled = sourceBlock.Header.BlockAccessListHash is not null;

                if (!balEnabled)
                {
                    if (sourceBal is not null || syncedBal is not null)
                    {
                        TestContext.Progress.WriteLine(
                            $"BAL debug block {blockNumber}: sourceBal={(sourceBal is null ? "null" : sourceBal.Length)}, " +
                            $"syncedBal={(syncedBal is null ? "null" : syncedBal.Length)}, " +
                            $"sourceBalHash={sourceBlock.Header.BlockAccessListHash}, " +
                            $"syncedBalHash={syncedBlock.Header.BlockAccessListHash}");
                    }

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(sourceBal, Is.Null, $"Source BAL should be absent before EIP-7928 at block {blockNumber}.");
                        Assert.That(syncedBal, Is.Null, $"Synced BAL should be absent before EIP-7928 at block {blockNumber}.");
                        Assert.That(syncedBlock.Header.BlockAccessListHash, Is.Null, $"BAL hash should be absent before EIP-7928 at block {blockNumber}.");
                    }
                    return;
                }

                if (sourceBal is null || syncedBal is null)
                {
                    TestContext.Progress.WriteLine(
                        $"BAL debug block {blockNumber}: sourceBal={(sourceBal is null ? "null" : sourceBal.Length)}, " +
                        $"syncedBal={(syncedBal is null ? "null" : syncedBal.Length)}, " +
                        $"syncedEncoded={(syncedBlock.EncodedBlockAccessList is null ? "null" : syncedBlock.EncodedBlockAccessList.Length)}, " +
                        $"syncedHasStore={blockAccessListStore.Exists(syncedBlock.Number, syncedBlock.Hash!)}, " +
                        $"headerBalHash={syncedBlock.Header.BlockAccessListHash}");
                }

                Assert.That(sourceBal, Is.Not.Null, $"Source BAL missing at block {blockNumber}.");
                Assert.That(syncedBal, Is.Not.Null, $"Synced BAL missing at block {blockNumber}.");
                Assert.That(syncedBal, Is.EqualTo(sourceBal), $"BAL mismatch at block {blockNumber}.");
                Assert.That(new Hash256(ValueKeccak.Compute(syncedBal!).Bytes), Is.EqualTo(syncedBlock.Header.BlockAccessListHash),
                    $"BAL hash mismatch at block {blockNumber}.");
            }
        }

        private static byte[]? GetBlockAccessListRlp(IBlockAccessListStore blockAccessListStore, long blockNumber, Hash256 blockHash)
        {
            using MemoryManager<byte>? rlp = blockAccessListStore.GetRlp(blockNumber, blockHash);
            return rlp?.Memory.ToArray();
        }
    }

    // For failing test when disconnect is disconnected. Make test fail faster instead of waiting for timeout.
    private class ImmediateDisconnectFailure : IDisconnectsAnalyzer
    {
        private string? DisconnectFailure = null;
        private readonly CancellationTokenSource _cts = new();

        public void ReportDisconnect(DisconnectReason reason, DisconnectType type, string details)
        {
            DisconnectFailure = $"{reason} {details}";
            _cts.Cancel();
        }

        public async Task WatchForDisconnection(Func<CancellationToken, Task> act, CancellationToken cancellationToken)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            try
            {
                await act(cts.Token);
                if (DisconnectFailure != null) Assert.Fail($"Disconnect detected. {DisconnectFailure}");
            }
            catch (OperationCanceledException)
            {
                if (DisconnectFailure == null) throw; // Timeout without disconnect
                Assert.Fail($"Disconnect detected. {DisconnectFailure}");
            }
        }
    }

    internal class BlockProcessorExceptionDetector
    {
        internal static void Configure(ContainerBuilder builder) =>
            builder.AddSingleton<BlockProcessorExceptionDetector>()
                .AddDecorator<IBlockProcessor, BlockProcessorInterceptor>();

        private Exception? BlockProcessingFailure;
        private CancellationTokenSource _cts = new();

        private void ReportException(Exception exception)
        {
            BlockProcessingFailure = exception;
            _cts.Cancel();
        }


        public async Task WatchForFailure(Func<CancellationToken, Task> act, CancellationToken cancellationToken)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            try
            {
                await act(cts.Token);
                if (BlockProcessingFailure != null) Assert.Fail($"Block processing failure detected. {BlockProcessingFailure}");
            }
            catch (OperationCanceledException)
            {
                if (BlockProcessingFailure == null) throw; // Timeout without disconnect
                Assert.Fail($"Block processing failure detected. {BlockProcessingFailure}");
            }
        }

        internal class BlockProcessorInterceptor(
            IBlockProcessor blockProcessor,
            BlockProcessorExceptionDetector blockProcessorExceptionDetector) : IBlockProcessor
        {
            public event Action? TransactionsExecuted
            {
                add => blockProcessor.TransactionsExecuted += value;
                remove => blockProcessor.TransactionsExecuted -= value;
            }

            public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options,
                IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
            {
                try
                {
                    return blockProcessor.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
                }
                catch (Exception ex)
                {
                    blockProcessorExceptionDetector.ReportException(ex);
                    throw;
                }
            }
        }
    }
}
