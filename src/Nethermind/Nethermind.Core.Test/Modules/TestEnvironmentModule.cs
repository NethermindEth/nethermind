// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Network.Discovery;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.State;
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

            .AddDecorator<ISyncConfig>((_, syncConfig) =>
            {
                syncConfig.GCOnFeedFinished = false;
                syncConfig.MultiSyncModeSelectorLoopTimerMs = 1;
                syncConfig.SyncDispatcherEmptyRequestDelayMs = 1;
                syncConfig.SyncDispatcherAllocateTimeoutMs = 1;
                return syncConfig;
            })
            .AddDecorator<INetworkConfig>((_, networkConfig) =>
            {
                networkConfig.DiscoveryDns = null;
                networkConfig.LocalIp ??= "127.0.0.1";
                networkConfig.ExternalIp ??= "127.0.0.1";
                networkConfig.RlpxHostShutdownCloseTimeoutMs = 1;
                return networkConfig;
            });
    }
}
