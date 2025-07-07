// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Events;
using Nethermind.Network;
using Nethermind.Network.Rlpx;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// Replaces nethermind runner during test. Hardcoded and need to be called manually.
/// Everything is lazy and not automatically injected on constructor, so if its not needed, its not constructed and running.
/// </summary>
public class PseudoNethermindRunner(IComponentContext ctx) : IAsyncDisposable
{
    private IBlockchainProcessor? _blockchainProcessor;
    private IBlockProducerRunner? _blockProducerRunner;
    private IRlpxHost? _rlpxHost;
    private ISessionMonitor? _sessionMonitor;
    private IDiscoveryApp? _discoveryApp;
    private IPeerPool? _peerPool;
    private IPeerManager? _peerManager;

    public async Task StartBlockProcessing(CancellationToken cancellationToken)
    {
        _blockProducerRunner ??= ctx.Resolve<IBlockProducerRunner>();
        _blockProducerRunner.Start();

        IMainProcessingContext mainBlockProcessingContext = ctx.Resolve<AutoMainProcessingContext>();
        _blockchainProcessor = mainBlockProcessingContext.BlockchainProcessor;
        _blockchainProcessor.Start();

        await PrepareGenesis(cancellationToken);
    }

    private async Task PrepareGenesis(CancellationToken cancellation)
    {
        if (_blockchainProcessor is null) await StartBlockProcessing(cancellation);

        IBlockTree blockTree = ctx.Resolve<IBlockTree>();
        if (blockTree.Genesis is not null) return;

        GenesisLoader genesisLoader = ctx.Resolve<GenesisLoader>();

        Task newHeadTask = Wait.ForEventCondition<BlockEventArgs>(
            cancellation,
            (h) => blockTree.NewHeadBlock += h,
            (h) => blockTree.NewHeadBlock -= h,
            (e) => true);

        Block genesis = genesisLoader.Load();
        blockTree.SuggestBlock(genesis);
        await newHeadTask;
    }

    public async Task StartNetwork(CancellationToken cancellationToken)
    {
        // This need the genesis so it need to be resolved after genesis was prepared.
        IBlockTree blockTree = ctx.Resolve<IBlockTree>();
        if (blockTree.Genesis is null) await PrepareGenesis(cancellationToken);

        // Protocol manager is what listen to rlpx for new connection which then send to sync peer pool.
        ctx.Resolve<IProtocolsManager>();

        if (_rlpxHost is null)
        {
            _rlpxHost = ctx.Resolve<IRlpxHost>();
            await _rlpxHost.Init();
        }

        // Sync peer pool has a loop that refresh the peer TD and header.
        ctx.Resolve<ISyncPeerPool>().Start();
        ctx.Resolve<ISynchronizer>().Start();
    }

    public async Task StartDiscovery(CancellationToken cancellationToken)
    {
        // Needed by peer manager
        if (_rlpxHost is null)
        {
            _rlpxHost = ctx.Resolve<IRlpxHost>();
            await _rlpxHost.Init();
        }

        if (_sessionMonitor is not null) return;
        await ctx.Resolve<IStaticNodesManager>().InitAsync();

        _discoveryApp = ctx.Resolve<IDiscoveryApp>();
        _ = _discoveryApp.StartAsync(); // Bootstrap is not blocking by default

        _peerPool = ctx.Resolve<IPeerPool>();
        _peerPool.Start();

        _peerManager = ctx.Resolve<IPeerManager>();
        _peerManager.Start();

        _sessionMonitor = ctx.Resolve<ISessionMonitor>();
        _sessionMonitor.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await (_blockchainProcessor?.StopAsync() ?? Task.CompletedTask);
        await (_blockProducerRunner?.StopAsync() ?? Task.CompletedTask);
        await (_rlpxHost?.Shutdown() ?? Task.CompletedTask);

        _sessionMonitor?.Stop();
        await (_discoveryApp?.StopAsync() ?? Task.CompletedTask);
        await (_peerPool?.StopAsync() ?? Task.CompletedTask);
        await (_peerManager?.StopAsync() ?? Task.CompletedTask);
    }
}
