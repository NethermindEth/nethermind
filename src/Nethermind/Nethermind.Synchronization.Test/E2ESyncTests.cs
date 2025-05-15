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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
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
        NoPruning
    }

    public static IEnumerable<TestFixtureParameters> CreateTestCases()
    {
        yield return new TestFixtureParameters(DbMode.Default, false);
        yield return new TestFixtureParameters(DbMode.Hash, false);
        yield return new TestFixtureParameters(DbMode.NoPruning, false);
        yield return new TestFixtureParameters(DbMode.Default, true);
        yield return new TestFixtureParameters(DbMode.Hash, true);
        yield return new TestFixtureParameters(DbMode.NoPruning, true);
    }

    private static TimeSpan SetupTimeout = TimeSpan.FromSeconds(20);
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
    /// Common code for all node
    /// </summary>
    private IContainer CreateNode(PrivateKey nodeKey, Action<IConfigProvider, ChainSpec> configurer)
    {
        IConfigProvider configProvider = new ConfigProvider();
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile("chainspec/foundation.json");

        // Set basefeepergas in genesis or it will fail 1559 validation.
        spec.Genesis.Header.BaseFeePerGas = 10.Wei();

        // Needed for generating spam state.
        spec.Genesis.Header.GasLimit = 1_000_000_000;
        spec.Allocations[_serverKey.Address] = new ChainSpecAllocation(300.Ether());

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

        // Always on, as the timestamp based fork activation always override block number based activation. However, the receipt
        // message serializer does not check the block header of the receipt for timestamp, only block number therefore it will
        // always not encode with Eip658, but the block builder always build with Eip658 as the latest fork activation
        // uses timestamp which is < than now.
        // TODO: Need to double check which code part does not pass in timestamp from header.
        spec.Parameters.Eip658Transition = 0;

        if (isPostMerge)
        {
            spec.Genesis.Header.Difficulty = 10000;

            IMergeConfig mergeConfig = configProvider.GetConfig<IMergeConfig>();
            mergeConfig.Enabled = true;
            mergeConfig.TerminalTotalDifficulty = "10000";
            mergeConfig.FinalTotalDifficulty = "10000";
        }

        configurer.Invoke(configProvider, spec);

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
        }

        var builder = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(spec, configProvider, new TestLogManager()))
            .AddModule(new TestEnvironmentModule(nodeKey, $"{nameof(E2ESyncTests)} {dbMode} {isPostMerge}"))
            .AddSingleton<IDisconnectsAnalyzer, ImmediateDisconnectFailure>()
            .AddSingleton<SyncTestContext>()
            .AddSingleton<ITestEnv, PreMergeTestEnv>()
            ;

        if (isPostMerge)
        {
            builder
                .AddModule(new MergeModule(
                    configProvider.GetConfig<ITxPoolConfig>(),
                    configProvider.GetConfig<IMergeConfig>(),
                    configProvider.GetConfig<IBlocksConfig>()
                ))
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
        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(SetupTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        PrivateKey serverKey = TestItem.PrivateKeyA;
        _serverKey = serverKey;
        _server = CreateNode(serverKey, (cfg, spec) =>
        {
            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = AllocatePort();
        });

        SyncTestContext serverCtx = _server.Resolve<SyncTestContext>();
        await serverCtx.StartBlockProcessing(cancellationToken);

        byte[] spam = Prepare.EvmCode
            .ForCreate2Of(
                Prepare.EvmCode
                    .PushData(100)
                    .PushData(100)
                    .Op(Instruction.SSTORE)
                    .PushData(100)
                    .PushData(101)
                    .Op(Instruction.SSTORE)
                    .PushData(100)
                    .Op(Instruction.SLOAD)
                    .PushData(101)
                    .Op(Instruction.SLOAD)
                    .PushData(102)
                    .Done)
            .Done;

        for (int i = 0; i < ChainLength; i++)
        {
            await serverCtx.BuildBlockWithCode([spam, spam, spam], cancellationToken);
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
        await using IContainer client = CreateNode(clientKey, (cfg, spec) =>
        {
            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = AllocatePort();
        });

        await client.Resolve<SyncTestContext>().SyncFromServer(_server, cancellationTokenSource.Token);
    }

    [Test]
    [Retry(5)]
    public async Task FastSync()
    {
        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);

        PrivateKey clientKey = TestItem.PrivateKeyC;
        await using IContainer client = CreateNode(clientKey, (cfg, spec) =>
        {
            SyncConfig syncConfig = (SyncConfig)cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;

            IBlockTree serverBlockTree = _server.Resolve<IBlockTree>();
            long serverHeadNumber = serverBlockTree.Head!.Number;
            BlockHeader pivot = serverBlockTree.FindHeader(serverHeadNumber - HeadPivotDistance)!;
            syncConfig.PivotHash = pivot.Hash!.ToString();
            syncConfig.PivotNumber = pivot.Number.ToString();
            syncConfig.PivotTotalDifficulty = pivot.TotalDifficulty!.Value.ToString();

            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = AllocatePort();
        });

        await client.Resolve<SyncTestContext>().SyncFromServer(_server, cancellationTokenSource.Token);
    }

    [Test]
    [Retry(5)]
    public async Task SnapSync()
    {
        if (dbMode == DbMode.Hash) Assert.Ignore("Hash db does not support snap sync");

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);

        PrivateKey clientKey = TestItem.PrivateKeyD;
        await using IContainer client = CreateNode(clientKey, (cfg, spec) =>
        {
            SyncConfig syncConfig = (SyncConfig)cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;
            syncConfig.SnapSync = true;

            IBlockTree serverBlockTree = _server.Resolve<IBlockTree>();
            long serverHeadNumber = serverBlockTree.Head!.Number;
            BlockHeader pivot = serverBlockTree.FindHeader(serverHeadNumber - HeadPivotDistance)!;
            syncConfig.PivotHash = pivot.Hash!.ToString();
            syncConfig.PivotNumber = pivot.Number.ToString();
            syncConfig.PivotTotalDifficulty = pivot.TotalDifficulty!.Value.ToString();

            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = AllocatePort();
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

            blockTree.SuggestBlock(blockProductionContext.CurrentBestBlock!).Should().Be(AddBlockResult.Added);

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
        MainBlockProcessingContext mainBlockProcessingContext,
        ITestEnv testEnv,
        IRlpxHost rlpxHost,
        PseudoNethermindRunner runner,
        ImmediateDisconnectFailure immediateDisconnectFailure)
    {
        // These check is really slow (it doubles the test time) so its disabled by default.
        private const bool CheckBlocksAndReceiptsContent = false;
        private const bool VerifyTrieOnFinished = false;

        private readonly BlockDecoder _blockDecoder = new BlockDecoder();
        private readonly ReceiptsMessageSerializer _receiptsMessageSerializer = new(specProvider);

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
            await rlpxHost.ConnectAsync(serverNode);
        }

        Dictionary<Address, UInt256> nonces = [];

        public async Task BuildBlockWithCode(byte[][] codes, CancellationToken cancellation)
        {
            // 1 000 000 000
            long gasLimit = 100000;

            Hash256 stateRoot = blockTree.Head?.StateRoot!;
            nonces.TryGetValue(nodeKey.Address, out UInt256 currentNonce);
            IReleaseSpec spec = specProvider.GetSpec((blockTree.Head?.Number) + 1 ?? 0, null);
            Transaction[] txs = codes.Select((byteCode) => Build.A.Transaction
                    .WithCode(byteCode)
                    .WithNonce(currentNonce++)
                    .WithGasLimit(gasLimit)
                    .WithGasPrice(10.GWei())
                    .SignedAndResolved(ecdsa, nodeKey, spec.IsEip155Enabled).TestObject)
                .ToArray();
            nonces[nodeKey.Address] = currentNonce;
            await testEnv.BuildBlockWithTxs(txs, cancellation);
        }

        private async Task VerifyHeadWith(IContainer server, CancellationToken cancellationToken)
        {
            IBlockProcessingQueue queue = mainBlockProcessingContext.BlockProcessingQueue;
            if (!queue.IsEmpty)
            {
                await Wait.ForEvent(cancellationToken,
                    e => queue.ProcessingQueueEmpty += e,
                    e => queue.ProcessingQueueEmpty -= e);
            }

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
                await runner.StartNetwork(token);
                await ConnectTo(server, token);
                await testEnv.SyncUntilFinished(server, token);
                await VerifyHeadWith(server, token);
                await VerifyAllBlocksAndReceipts(server, token);
            }, cancellationToken);
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
}
