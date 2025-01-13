// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Network.Config;
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

namespace Nethermind.Synchronization.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture(NodeMode.Default)]
[TestFixture(NodeMode.Hash)]
[TestFixture(NodeMode.NoPruning)]
public class E2ESyncTests(E2ESyncTests.NodeMode mode)
{
    public enum NodeMode
    {
        Default,
        Hash,
        NoPruning
    }

    private int _portNumber = 0;

    private static TimeSpan SetupTimeout = TimeSpan.FromSeconds(10);
    private static TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    PrivateKey _serverKey = TestItem.PrivateKeyA;
    IContainer _server = null!;

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
        ChainSpec spec = new ChainSpecLoader(new EthereumJsonSerializer()).LoadEmbeddedOrFromFile("chainspec/foundation.json", default);

        // Set basefeepergas in genesis or it will fail 1559 validation.
        spec.Genesis.Header.BaseFeePerGas = 1.GWei();

        // Disable as the built block always don't have withdrawal (it came from engine) so it fail validation.
        spec.Parameters.Eip4895TransitionTimestamp = null;

        // 4844 add BlobGasUsed which in the header decoder also imply WithdrawalRoot which would be set to 0 instead of null
        // which become invalid when using block body with null withdrawal.
        // Basically, these need merge block builder, or it will fail block validation on download.
        spec.Parameters.Eip4844TransitionTimestamp = null;

        // Always on, as the timestamp based fork activation always override block number based activation. However, the receipt
        // message serializer does not check the block header of the receipt for timestamp, only block number therefore it will
        // always not encode with Eip658, but the block builder always build with Eip658 as the latest fork activation
        // uses timestamp which is < than now.
        // TODO: Need to double check which code part does not pass in timestamp from header.
        spec.Parameters.Eip658Transition = 0;

        // Needed for generating spam state.
        spec.Genesis.Header.GasLimit = 100000000;
        spec.Allocations[_serverKey.Address] = new ChainSpecAllocation(30.Ether());

        configurer.Invoke(configProvider, spec);

        switch (mode)
        {
            case NodeMode.Default:
                // Um... nothing?
                break;
            case NodeMode.Hash:
            {
                IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
                initConfig.StateDbKeyScheme = INodeStorage.KeyScheme.Hash;
                break;
            }
            case NodeMode.NoPruning:
            {
                IPruningConfig pruningConfig = configProvider.GetConfig<IPruningConfig>();
                pruningConfig.Mode = PruningMode.None;
                break;
            }
        }

        return new ContainerBuilder()
            .AddModule(new PsudoNethermindModule(configProvider, spec))
            .AddModule(new TestEnvironmentModule(nodeKey, nameof(E2ESyncTests) + mode))
            .AddSingleton<SyncTestContext>()
            .Build();
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
                    .PushData(102)
                    .Op(Instruction.SSTORE)
                    .PushData(100)
                    .PushData(103)
                    .Op(Instruction.SSTORE)
                    .PushData(100)
                    .Op(Instruction.SLOAD)
                    .PushData(101)
                    .Op(Instruction.SLOAD)
                    .PushData(102)
                    .Op(Instruction.SLOAD)
                    .PushData(103)
                    .Op(Instruction.SLOAD)
                    .Done)
            .Done;

        for (int i = 0; i < 1000; i++)
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
    public async Task FastSync()
    {
        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);

        PrivateKey clientKey = TestItem.PrivateKeyC;
        await using IContainer client = CreateNode(clientKey, (cfg, spec) =>
        {
            SyncConfig syncConfig = (SyncConfig) cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;

            IBlockTree serverBlockTree = _server.Resolve<IBlockTree>();
            long serverHeadNumber = serverBlockTree.Head!.Number;
            BlockHeader pivot = serverBlockTree.FindHeader(serverHeadNumber - 500)!;
            syncConfig.PivotHash = pivot.Hash!.ToString();
            syncConfig.PivotNumber = pivot.Number.ToString();
            syncConfig.PivotTotalDifficulty = pivot.TotalDifficulty!.Value.ToString();

            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = AllocatePort();
        });

        await client.Resolve<SyncTestContext>().SyncFromServer(_server, cancellationTokenSource.Token);
    }

    [Test]
    public async Task SnapSync()
    {
        if (mode == NodeMode.Hash) Assert.Ignore("Hash db does not support snap sync");

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);

        PrivateKey clientKey = TestItem.PrivateKeyD;
        await using IContainer client = CreateNode(clientKey, (cfg, spec) =>
        {
            SyncConfig syncConfig = (SyncConfig) cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;
            syncConfig.SnapSync = true;

            IBlockTree serverBlockTree = _server.Resolve<IBlockTree>();
            long serverHeadNumber = serverBlockTree.Head!.Number;
            BlockHeader pivot = serverBlockTree.FindHeader(serverHeadNumber - 500)!;
            syncConfig.PivotHash = pivot.Hash!.ToString();
            syncConfig.PivotNumber = pivot.Number.ToString();
            syncConfig.PivotTotalDifficulty = pivot.TotalDifficulty!.Value.ToString();

            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = AllocatePort();
        });

        await client.Resolve<SyncTestContext>().SyncFromServer(_server, cancellationTokenSource.Token);
    }

    private class SyncTestContext
    {
        // This check is really slow (it doubles the test time) so its disabled by default.
        private const bool CheckBlocksAndReceiptsContent = false;

        private readonly PrivateKey _nodeKey;

        private readonly IWorldStateManager _worldStateManager;
        private readonly ITxPool _txPool;
        private readonly ISpecProvider _specProvider;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ManualTimestamper _timestamper;
        private readonly IManualBlockProductionTrigger _blockProductionTrigger;
        private readonly MainBlockProcessingContext _mainBlockProcessingContext;
        private readonly IRlpxHost _rlpxHost;
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly BlockDecoder _blockDecoder = new BlockDecoder();
        private readonly ReceiptsMessageSerializer _receiptsMessageSerializer;
        private readonly PsudoNethermindRunner _runner;

        public SyncTestContext(
            [KeyFilter(TestEnvironmentModule.NodeKey)] PrivateKey nodeKey,
            IWorldStateManager worldStateManager,
            ISpecProvider specProvider,
            IEthereumEcdsa ecdsa,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ManualTimestamper timestamper,
            IManualBlockProductionTrigger blockProductionTrigger,
            MainBlockProcessingContext mainBlockProcessingContext,
            ITxPool txPool,
            ISyncModeSelector syncModeSelector,
            IRlpxHost rlpxHost,
            PsudoNethermindRunner runner
        )
        {
            _txPool = txPool;
            _nodeKey = nodeKey;
            _worldStateManager = worldStateManager;
            _mainBlockProcessingContext = mainBlockProcessingContext;
            _specProvider = specProvider;
            _ecdsa = ecdsa;
            _blockTree = blockTree;
            _receiptStorage = receiptStorage;
            _timestamper = timestamper;
            _blockProductionTrigger = blockProductionTrigger;
            _syncModeSelector = syncModeSelector;
            _rlpxHost = rlpxHost;
            _runner = runner;
            _receiptsMessageSerializer = new ReceiptsMessageSerializer(specProvider);
        }

        public async Task StartBlockProcessing(CancellationToken cancellationToken)
        {
            await _runner.StartBlockProcessing(cancellationToken);
        }

        public async Task StartNetwork(CancellationToken cancellationToken)
        {
            await _runner.StartNetwork(cancellationToken);
        }

        private async Task ConnectTo(IContainer server, CancellationToken cancellationToken)
        {
            IEnode serverEnode = server.Resolve<IEnode>();
            Node serverNode = new Node(serverEnode.PublicKey, new IPEndPoint(serverEnode.HostIp, serverEnode.Port));
            await _rlpxHost.ConnectAsync(serverNode);
        }

        public async Task BuildBlockWithCode(byte[][] codes, CancellationToken cancellation)
        {
            // 1 000 000 000
            long gasLimit = 100000;

            Hash256 stateRoot = _blockTree.Head?.StateRoot!;
            UInt256 currentNonce = _worldStateManager.GlobalStateReader.GetNonce(stateRoot, _nodeKey.Address);
            IReleaseSpec spec = _specProvider.GetSpec((_blockTree.Head?.Number) + 1 ?? 0, null);
            Transaction[] txs = codes.Select((byteCode) => Build.A.Transaction
                    .WithCode(byteCode)
                    .WithNonce(currentNonce++)
                    .WithGasLimit(gasLimit)
                    .WithGasPrice(10.GWei())
                    .SignedAndResolved(_ecdsa, _nodeKey, spec.IsEip155Enabled).TestObject)
                .ToArray();

            await BuildBlockWithTxs(txs, cancellation);
        }

        private async Task BuildBlockWithTxs(Transaction[] transactions, CancellationToken cancellation)
        {
            Task newBlockTask = Wait.ForEventCondition<BlockReplacementEventArgs>(
                cancellation,
                (h) => _blockTree.BlockAddedToMain += h,
                (h) => _blockTree.BlockAddedToMain -= h,
                (e) => true);

            AcceptTxResult[] txResults = transactions.Select(t => _txPool.SubmitTx(t, TxHandlingOptions.None)).ToArray();
            foreach (AcceptTxResult acceptTxResult in txResults)
            {
                acceptTxResult.Should().Be(AcceptTxResult.Accepted);
            }

            _timestamper.Add(TimeSpan.FromSeconds(1));
            await _blockProductionTrigger.BuildBlock();
            await newBlockTask;
        }

        private async Task SyncUntilFinished(CancellationToken cancellationToken)
        {
            if (_syncModeSelector.Current == SyncMode.WaitingForBlock) return;

            await Wait.ForEventCondition<SyncModeChangedEventArgs>(cancellationToken,
                (e) => _syncModeSelector.Changed += e,
                (e) => _syncModeSelector.Changed += e,
                (evt) => evt.Current == SyncMode.WaitingForBlock);
        }

        private async Task VerifyHeadWith(IContainer server, CancellationToken cancellationToken)
        {
            IBlockProcessingQueue queue = _mainBlockProcessingContext.BlockProcessingQueue;
            if (!queue.IsEmpty)
            {
                await Wait.ForEvent(cancellationToken,
                    e => queue.ProcessingQueueEmpty += e,
                    e => queue.ProcessingQueueEmpty -= e);
            }

            IBlockTree otherBlockTree = server.Resolve<IBlockTree>();
            AssertBlockEqual(_blockTree.Head!, otherBlockTree.Head!);

            IWorldStateManager worldStateManager = server.Resolve<IWorldStateManager>();
            worldStateManager.VerifyTrie(_blockTree.Head!.Header, cancellationToken).Should().BeTrue();
        }

        private ValueTask VerifyAllBlocksAndReceipts(IContainer server, CancellationToken cancellationToken)
        {
            IBlockTree otherBlockTree = server.Resolve<IBlockTree>();
            IReceiptStorage otherReceiptStorage = server.Resolve<IReceiptStorage>();

            for (int i = 0; i < otherBlockTree.Head?.Number; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Block clientBlock = _blockTree.FindBlock(i)!;
                TxReceipt[] clientReceipts = _receiptStorage.Get(clientBlock);
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
            await _runner.StartNetwork(cancellationToken);

            await ConnectTo(server, cancellationToken);
            await SyncUntilFinished(cancellationToken);
            await VerifyHeadWith(server, cancellationToken);
            await VerifyAllBlocksAndReceipts(server, cancellationToken);
        }
    }
}
