// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Test;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// Module that set up test environment which should make nethermind works without doing any actual IO.
/// </summary>
/// <param name="nodeKey"></param>
public class TestEnvironmentModule(PrivateKey nodeKey, string? networkGroup) : Module
{
    public const string NodeKey = "NodeKey";

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<ILogManager>(new TestLogManager(LogLevel.Error)) // Limbologs actually have IsTrace set to true, so actually slow.
            .AddSingleton<IDbProvider>(TestMemDbProvider.Init())
            // These two dont use db provider
            .AddKeyedSingleton<IFullDb>(DbNames.PeersDb, (_) => new MemDb())
            .AddKeyedSingleton<IFullDb>(DbNames.DiscoveryNodes, (_) => new MemDb())
            .AddSingleton<IFileStoreFactory>(new InMemoryDictionaryFileStoreFactory())
            .AddSingleton<IChannelFactory, INetworkConfig>(networkConfig => new LocalChannelFactory(networkGroup ?? nameof(TestEnvironmentModule), networkConfig))

            .AddSingleton<PseudoNethermindRunner>()
            .AddSingleton<ISealer>(new NethDevSealEngine(nodeKey.Address))
            .AddSingleton<ITimestamper, ManualTimestamper>()
            .AddSingleton<IIPResolver, FixedIpResolver>()

            .AddKeyedSingleton<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey, new InsecureProtectedPrivateKey(nodeKey))
            .AddSingleton<IEnode, INetworkConfig>(networkConfig =>
            {
                IPAddress ipAddress = networkConfig.ExternalIp is not null ? IPAddress.Parse(networkConfig.ExternalIp) : IPAddress.Loopback;
                return new Enode(nodeKey.PublicKey, ipAddress, networkConfig.P2PPort);
            })
            .AddKeyedSingleton(NodeKey, nodeKey)

            .AddSingleton<IChainHeadInfoProvider, IComponentContext>((ctx) =>
            {
                ISpecProvider specProvider = ctx.Resolve<ISpecProvider>();
                IBlockTree blockTree = ctx.Resolve<IBlockTree>();
                IStateReader stateReader = ctx.Resolve<IStateReader>();
                ICodeInfoRepository codeInfoRepository = ctx.ResolveNamed<ICodeInfoRepository>(nameof(IWorldStateManager.GlobalWorldState));
                return new ChainHeadInfoProvider(specProvider, blockTree, stateReader, codeInfoRepository)
                {
                    // It just need to override this.
                    HasSynced = true
                };
            })

            // Useful for passing into another node's ISyncPeerPool.
            // Act like a connected sync peer without going through actual network and RLPs.
            .Add<SyncPeerMock>(ctx =>
            {
                IBlockTree blockTree = ctx.Resolve<IBlockTree>();
                ISyncServer syncServer = ctx.Resolve<ISyncServer>();
                IEnode enode = ctx.Resolve<IEnode>();
                IWorldStateManager worldStateManager = ctx.Resolve<IWorldStateManager>();
                ISnapSyncPeer? snapSyncPeer = null;
                if (worldStateManager.SnapServer is not null)
                {
                    snapSyncPeer = new MockSnapSyncPeer(worldStateManager.SnapServer);
                }

                return new SyncPeerMock(blockTree, syncServer, enode.PublicKey, snapSyncPeer: snapSyncPeer);
            })

            .AddDecorator<ISyncConfig>((_, syncConfig) =>
            {
                syncConfig.GCOnFeedFinished = false;
                syncConfig.MultiSyncModeSelectorLoopTimerMs = 1;
                syncConfig.SyncDispatcherEmptyRequestDelayMs = 1;
                syncConfig.SyncDispatcherAllocateTimeoutMs = 1;
                syncConfig.MaxProcessingThreads = Math.Min(8, Environment.ProcessorCount);
                return syncConfig;
            })
            .AddDecorator<IBlocksConfig>((_, blocksConfig) =>
            {
                blocksConfig.PreWarmStateConcurrency = Math.Min(4, Environment.ProcessorCount);
                return blocksConfig;
            })
            .AddDecorator<INetworkConfig>((_, networkConfig) =>
            {
                networkConfig.DiscoveryDns = null;
                networkConfig.LocalIp ??= "127.0.0.1";
                networkConfig.ExternalIp ??= "127.0.0.1";
                networkConfig.RlpxHostShutdownCloseTimeoutMs = 1;
                return networkConfig;
            })
            .AddDecorator<IPruningConfig>((_, pruningConfig) =>
            {
                pruningConfig.CacheMb = 8;
                pruningConfig.DirtyCacheMb = 4;
                pruningConfig.DirtyNodeShardBit = 1;
                return pruningConfig;
            })
            ;
    }
}
