// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Network;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Test.Modules;
using Nethermind.TxPool;

namespace Nethermind.Synchronization.Test;

public class BlockchainTestContext: IAsyncDisposable
{
    private readonly PrivateKey _nodeKey;

    private readonly IWorldStateManager _worldStateManager;
    private readonly ITxPool _txPool;
    private readonly ISpecProvider _specProvider;
    private readonly IEthereumEcdsa _ecdsa;
    private readonly IBlockTree _blockTree;
    private readonly ManualTimestamper _timestamper;
    private readonly IManualBlockProductionTrigger _blockProductionTrigger;
    private readonly BlockProcessingModule.MainBlockProcessingContext _mainBlockProcessingContext;
    private readonly IBlockProducerRunner _blockProducerRunner;
    private readonly Func<IProtocolsManager> _protocolsManagerFactory;
    private readonly IRlpxHost _rlpxHost;
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly ISynchronizer _synchronizer;
    private readonly ISyncPeerPool _syncPeerPool;

    private readonly BlockDecoder _blockDecoder = new BlockDecoder();

    public BlockchainTestContext(
        [KeyFilter(TestEnvironmentModule.NodeKey)] PrivateKey nodeKey,
        IWorldStateManager worldStateManager,
        ISpecProvider specProvider,
        IEthereumEcdsa ecdsa,
        IBlockTree blockTree,
        ManualTimestamper timestamper,
        IManualBlockProductionTrigger blockProductionTrigger,
        BlockProcessingModule.MainBlockProcessingContext mainBlockProcessingContext,
        ITxPool txPool,
        IBlockProducerRunner blockProducerRunner,
        ProducedBlockSuggester producedBlockSuggester, // Need to be instantiated,
        Func<IProtocolsManager> protocolsManagerFactory,
        ISyncModeSelector syncModeSelector,
        ISynchronizer synchronizer,
        IRlpxHost rlpxHost,
        ISyncPeerPool syncPeerPool
    )
    {
        _txPool = txPool;
        _nodeKey = nodeKey;
        _worldStateManager = worldStateManager;
        _mainBlockProcessingContext = mainBlockProcessingContext;
        _specProvider = specProvider;
        _ecdsa = ecdsa;
        _blockTree = blockTree;
        _timestamper = timestamper;
        _blockProductionTrigger = blockProductionTrigger;
        _blockProducerRunner = blockProducerRunner;
        _protocolsManagerFactory = protocolsManagerFactory;
        _syncModeSelector = syncModeSelector;
        _synchronizer = synchronizer;
        _rlpxHost = rlpxHost;
        _syncPeerPool = syncPeerPool;
    }

    public async Task StartBlockProcessing(CancellationToken cancellationToken)
    {
        _blockProducerRunner.Start();
        _mainBlockProcessingContext.BlockchainProcessor.Start();

        await PrepareGenesis(cancellationToken);
    }

    public async Task StartNetwork(CancellationToken cancellationToken)
    {
        // This need the genesis so it need to be resolved after genesis was prepared.
        // Protocol manager is what listen to rlpx for new connection whicch then send to sync peer pool.
        _protocolsManagerFactory();

        await _rlpxHost.Init();

        // Sync peer pool has a loop that refresh the peer TD and header.
        _syncPeerPool.Start();
    }

    public async Task ConnectTo(IContainer server, CancellationToken cancellationToken)
    {
        IEnode serverEnode = server.Resolve<IEnode>();
        Node serverNode = new Node(serverEnode.PublicKey, new IPEndPoint(serverEnode.HostIp, serverEnode.Port));
        await _rlpxHost.ConnectAsync(serverNode);
    }

    private async Task PrepareGenesis(CancellationToken cancellation)
    {
        Task newHeadTask = Wait.ForEventCondition<BlockEventArgs>(
            cancellation,
            (h) => _blockTree.NewHeadBlock += h,
            (h) => _blockTree.NewHeadBlock -= h,
            (e) => true);

        Block genesis = _mainBlockProcessingContext.GenesisLoader.Load();
        _blockTree.SuggestBlock(genesis);
        await newHeadTask;
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

    public async ValueTask DisposeAsync()
    {
        await _mainBlockProcessingContext.BlockchainProcessor.StopAsync();
        await _blockProducerRunner.StopAsync();
        await _rlpxHost.Shutdown();
    }

    public async Task SyncUntilFinished(CancellationToken cancellationToken)
    {
        _synchronizer.Start();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_syncModeSelector.Current == SyncMode.WaitingForBlock) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    public async Task VerifyHeadWith(IContainer server, CancellationToken cancellationToken)
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

    private void AssertBlockEqual(Block block1, Block block2)
    {
        block1 = ReEncodeBlock(block1);
        block2 = ReEncodeBlock(block2);

        block1.Should().BeEquivalentTo(block2, static o => o
            .ComparingByMembers<Transaction>()
            .Using<Memory<byte>>(static ctx => ctx.Subject.AsArray().Should().BeEquivalentTo(ctx.Expectation.AsArray()))
            .WhenTypeIs<Memory<byte>>());
    }

    private Block ReEncodeBlock(Block block)
    {
        using var stream = _blockDecoder.EncodeToNewNettyStream(block);
        return _blockDecoder.Decode(stream)!;
    }
}
