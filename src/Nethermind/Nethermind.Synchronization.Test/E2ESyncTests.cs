// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
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

    private static TimeSpan SetupTimeout = TimeSpan.FromSeconds(60);
    private static TimeSpan TestTimeout = TimeSpan.FromSeconds(60);
    private const int ChainLength = 1000;
    private const int HeadPivotDistance = 500;

    private int _portNumber = 0;
    private PrivateKey _serverKey = TestItem.PrivateKeyA;
    private IContainer _server = null!;

    private int AllocatePort()
    {
        return Interlocked.Increment(ref _portNumber);
    }

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
        EthashChainSpecEngineParameters ethashParams = spec.EngineChainSpecParametersProvider
            .GetChainSpecParameters<EthashChainSpecEngineParameters>();
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
    private async Task<IContainer> CreateNode(PrivateKey nodeKey, Func<IConfigProvider, ChainSpec, Task> configurer)
    {
        IConfigProvider configProvider = new ConfigProvider();
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile("chainspec/foundation.json");

        // Set basefeepergas in genesis or it will fail 1559 validation.
        spec.Genesis.Header.BaseFeePerGas = 10.Wei;

        // Needed for generating spam state.
        spec.Genesis.Header.GasLimit = 1_000_000_000;
        spec.Allocations[_serverKey.Address] = new ChainSpecAllocation(300.Ether);

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

        switch (dbMode)
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

        var builder = new ContainerBuilder()
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
            ManualTimestamper timestamper = new(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
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

        return builder.Build();
    }

    [OneTimeSetUp]
    public async Task SetupServer()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.CancelAfter(SetupTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        PrivateKey serverKey = TestItem.PrivateKeyA;
        _serverKey = serverKey;
        _server = await CreateNode(serverKey, (cfg, spec) =>
        {
            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = AllocatePort();
            // Disable IP filtering for E2E tests as all nodes run on localhost
            networkConfig.FilterPeersByRecentIp = false;
            networkConfig.FilterDiscoveryNodesByRecentIp = false;
            return Task.CompletedTask;
        });

        SyncTestContext serverCtx = _server.Resolve<SyncTestContext>();
        await serverCtx.StartBlockProcessing(cancellationToken);

        for (int i = 0; i < ChainLength; i++)
        {
            await serverCtx.BuildBlockWithStorage(i, cancellationToken);
        }

        await serverCtx.StartNetwork(cancellationToken);
    }

    [OneTimeTearDown]
    public async Task TearDownServer()
    {
        await _server.DisposeAsync();
    }

    [Test]
    [Retry(5)]
    public async Task FullSync()
    {
        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);

        PrivateKey clientKey = TestItem.PrivateKeyB;
        await using IContainer client = await CreateNode(clientKey, (cfg, spec) =>
        {
            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = AllocatePort();
            // Disable IP filtering for E2E tests as all nodes run on localhost
            networkConfig.FilterPeersByRecentIp = false;
            networkConfig.FilterDiscoveryNodesByRecentIp = false;
            return Task.CompletedTask;
        });

        await client.Resolve<SyncTestContext>().SyncFromServer(_server, cancellationTokenSource.Token);
    }

    [Test]
    [Retry(5)]
    public async Task FastSync()
    {
        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);

        PrivateKey clientKey = TestItem.PrivateKeyC;
        await using IContainer client = await CreateNode(clientKey, async (cfg, spec) =>
        {
            SyncConfig syncConfig = (SyncConfig)cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;

            await SetPivot(syncConfig, cancellationTokenSource.Token);

            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = AllocatePort();
            // Disable IP filtering for E2E tests as all nodes run on localhost
            networkConfig.FilterPeersByRecentIp = false;
            networkConfig.FilterDiscoveryNodesByRecentIp = false;
        });

        await client.Resolve<SyncTestContext>().SyncFromServer(_server, cancellationTokenSource.Token);
    }

    private async Task SetPivot(SyncConfig syncConfig, CancellationToken cancellationToken)
    {
        IBlockProcessingQueue blockProcessingQueue = _server.Resolve<IBlockProcessingQueue>();
        await blockProcessingQueue.WaitForBlockProcessing(cancellationToken);
        IBlockTree serverBlockTree = _server.Resolve<IBlockTree>();
        long serverHeadNumber = serverBlockTree.Head!.Number;
        BlockHeader pivot = serverBlockTree.FindHeader(serverHeadNumber - HeadPivotDistance)!;
        syncConfig.PivotHash = pivot.Hash!.ToString();
        syncConfig.PivotNumber = pivot.Number;
        syncConfig.PivotTotalDifficulty = pivot.TotalDifficulty!.Value.ToString();
    }

    [Test]
    [Retry(5)]
    public async Task SnapSync()
    {
        if (dbMode == DbMode.Hash) Assert.Ignore("Hash db does not support snap sync");

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
            // Disable IP filtering for E2E tests as all nodes run on localhost
            networkConfig.FilterPeersByRecentIp = false;
            networkConfig.FilterDiscoveryNodesByRecentIp = false;
        });

        await client.Resolve<SyncTestContext>().SyncFromServer(_server, cancellationTokenSource.Token);
    }

    // Post and pre merge have slightly different operation for these.
    private interface ITestEnv
    {
        Task BuildBlockWithTxs(Transaction[] transactions, CancellationToken cancellation);
        Task SyncUntilFinished(IContainer server, CancellationToken cancellationToken);
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
                acceptTxResult.Should().Be(AcceptTxResult.Accepted);
            }

            timestamper.Add(TimeSpan.FromSeconds(1));
            try
            {
                (await manualBlockProductionTrigger.BuildBlock()).Should().NotBeNull();
                await newBlockTask;
            }
            catch (Exception e)
            {
                Assert.Fail($"Error building block. Head: {blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}, {e}");
            }
        }


        public virtual async Task SyncUntilFinished(IContainer server, CancellationToken cancellationToken)
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
        ITestEnv preMergeTestEnv
    ) : ITestEnv
    {
        public async Task BuildBlockWithTxs(Transaction[] transactions, CancellationToken cancellation)
        {
            Task newBlockTask = blockTree.WaitForNewBlock(cancellation);

            AcceptTxResult[] txResults = transactions.Select(t => txPool.SubmitTx(t, TxHandlingOptions.None)).ToArray();
            foreach (AcceptTxResult acceptTxResult in txResults)
            {
                acceptTxResult.Should().Be(AcceptTxResult.Accepted);
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
            payloadId.Should().NotBeNullOrEmpty();

            IBlockProductionContext? blockProductionContext = await payloadPreparationService.GetPayload(payloadId!, skipCancel: true);
            blockProductionContext.Should().NotBeNull();
            blockProductionContext!.CurrentBestBlock.Should().NotBeNull();

            (await blockTree.SuggestBlockAsync(blockProductionContext.CurrentBestBlock!)).Should().Be(AddBlockResult.Added);

            await newBlockTask;
        }

        public async Task SyncUntilFinished(IContainer server, CancellationToken cancellationToken)
        {
            IBlockTree otherBlockTree = server.Resolve<IBlockTree>();
            Block finalizedBlock = otherBlockTree.FindBlock(otherBlockTree.Head!.Number - 250)!;
            Block headBlock = otherBlockTree.Head!;
            blockCacheService.BlockCache.TryAdd(new Hash256AsKey(finalizedBlock.Hash!), finalizedBlock);
            blockCacheService.BlockCache.TryAdd(new Hash256AsKey(headBlock.Hash!), headBlock);
            blockCacheService.FinalizedHash = finalizedBlock.Hash!;

            await preMergeTestEnv.WaitForSyncMode(mode => mode != SyncMode.UpdatingPivot, cancellationToken);
            mergeSyncController.TryInitBeaconHeaderSync(headBlock.Header);

            await preMergeTestEnv.SyncUntilFinished(server, cancellationToken);
        }

        public async Task WaitForSyncMode(Func<SyncMode, bool> modeCheck, CancellationToken cancellationToken)
        {
            await preMergeTestEnv.WaitForSyncMode(modeCheck, cancellationToken);
        }
    }

    private class SyncTestContext(
        [KeyFilter(TestEnvironmentModule.NodeKey)] PrivateKey nodeKey,
        ISpecProvider specProvider,
        IEthereumEcdsa ecdsa,
        IBlockTree blockTree,
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

        private readonly BlockDecoder _blockDecoder = new BlockDecoder();
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

        public async Task StartBlockProcessing(CancellationToken cancellationToken)
        {
            await runner.StartBlockProcessing(cancellationToken);
        }

        public async Task StartNetwork(CancellationToken cancellationToken)
        {
            await runner.StartNetwork(cancellationToken);
        }

        private async Task ConnectTo(IContainer server, CancellationToken cancellationToken)
        {
            IEnode serverEnode = server.Resolve<IEnode>();
            Node serverNode = new Node(serverEnode.PublicKey, new IPEndPoint(serverEnode.HostIp, serverEnode.Port));
            if (!await rlpxHost.ConnectAsync(serverNode))
            {
                throw new NetworkingException($"Failed to connect to {serverNode:s}", NetworkExceptionType.TargetUnreachable);
            }
        }

        Dictionary<Address, UInt256> nonces = [];

        public async Task BuildBlockWithCode(byte[][] codes, CancellationToken cancellation)
        {
            // 1 000 000 000
            long gasLimit = 1_000_000;

            nonces.TryGetValue(nodeKey.Address, out UInt256 currentNonce);
            IReleaseSpec spec = specProvider.GetSpec((blockTree.Head?.Number) + 1 ?? 0, null);
            Transaction[] txs = codes.Select((byteCode) => Build.A.Transaction
                    .WithCode(byteCode)
                    .WithNonce(currentNonce++)
                    .WithGasLimit(gasLimit)
                    .WithGasPrice(10.GWei)
                    .SignedAndResolved(ecdsa, nodeKey, spec.IsEip155Enabled).TestObject)
                .ToArray();
            nonces[nodeKey.Address] = currentNonce;
            await testEnv.BuildBlockWithTxs(txs, cancellation);
        }

        public async Task BuildBlockWithStorage(int blockNumber, CancellationToken cancellation)
        {
            long gasLimit = 200_000;

            nonces.TryGetValue(nodeKey.Address, out UInt256 currentNonce);
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

            nonces[nodeKey.Address] = currentNonce;
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
                worldStateManager.VerifyTrie(blockTree.Head!.Header, cancellationToken).Should().BeTrue();
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
                clientBlock.Should().NotBeNull();
                clientReceipts.Should().NotBeNull();

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
            using NettyRlpStream stream1 = _blockDecoder.EncodeToNewNettyStream(block1);
            using NettyRlpStream stream2 = _blockDecoder.EncodeToNewNettyStream(block2);

            stream1.AsSpan().ToArray().Should().BeEquivalentTo(stream2.AsSpan().ToArray());
        }

        private void AssertReceiptsEqual(TxReceipt[] receipts1, TxReceipt[] receipts2)
        {
            // The network encoding is not the same as storage encoding.
            EncodeReceipts(receipts1).Should().BeEquivalentTo(EncodeReceipts(receipts2));
        }

        private byte[] EncodeReceipts(TxReceipt[] receipts)
        {
            TxReceipt[][] wrappedReceipts = new[] { receipts };
            using ReceiptsMessage asReceiptsMessage = new ReceiptsMessage(wrappedReceipts.ToPooledList());

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

        public async Task SyncFromServer(IContainer server, CancellationToken cancellationToken)
        {
            await immediateDisconnectFailure.WatchForDisconnection(async (token) =>
            {
                await blockProcessorExceptionDetector.WatchForFailure(async (token) =>
                {
                    await runner.StartNetwork(token);
                    await ConnectTo(server, token);
                    await testEnv.SyncUntilFinished(server, token);
                    await VerifyHeadWith(server, token);
                    await VerifyAllBlocksAndReceipts(server, token);
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
    }

    // For failing test when disconnect is disconnected. Make test fail faster instead of waiting for timeout.
    private class ImmediateDisconnectFailure : IDisconnectsAnalyzer
    {
        private string? DisconnectFailure = null;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        public void ReportDisconnect(DisconnectReason reason, DisconnectType type, string details)
        {
            DisconnectFailure = $"{reason.ToString()} {details}";
            _cts.Cancel();
        }

        public async Task WatchForDisconnection(Func<CancellationToken, Task> act, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
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
        internal static void Configure(ContainerBuilder builder)
        {
            builder.AddSingleton<BlockProcessorExceptionDetector>()
                .AddDecorator<IBlockProcessor, BlockProcessorInterceptor>();
        }

        private Exception? BlockProcessingFailure;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        private void ReportException(Exception exception)
        {
            BlockProcessingFailure = exception;
            _cts.Cancel();
        }

        public async Task WatchForFailure(Func<CancellationToken, Task> act, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
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
