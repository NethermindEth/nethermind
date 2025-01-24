// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Autofac;
using Nethermind.Api;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Dns;
using Nethermind.Network.Enr;
using Nethermind.Network.StaticNodes;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Core.Test.Modules;

public class DiscoveryModule(IInitConfig initConfig, INetworkConfig networkConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            // Enr discovery uses DNS to get some bootnodes.
            // TODO: Node source to discovery 4 feeder.
            .AddSingleton<EnrDiscovery, IEthereumEcdsa, ILogManager>((ethereumEcdsa, logManager) =>
            {
                // I do not use the key here -> API is broken - no sense to use the node signer here
                NodeRecordSigner nodeRecordSigner = new(ethereumEcdsa, new PrivateKeyGenerator().Generate());
                EnrRecordParser enrRecordParser = new(nodeRecordSigner);
                return new EnrDiscovery(enrRecordParser, networkConfig, logManager); // initialize with a proper network
            })

            // Uses by RPC also.
            .AddSingleton<IStaticNodesManager, ILogManager>((logManager) => new StaticNodesManager(initConfig.StaticNodesPath, logManager))
            // This load from file.
            .AddSingleton<NodesLoader>()

            .Bind<INodeSource, IStaticNodesManager>()
            .Bind<INodeSource, NodesLoader>()
            .AddComposite<INodeSource, CompositeNodeSource>()

            // The actual thing that uses the INodeSource(s)
            .AddSingleton<IPeerPool, PeerPool>()
            .AddSingleton<IPeerManager, PeerManager>()

            // Some config migration
            .AddDecorator<INetworkConfig>((ctx, networkConfig) =>
            {
                ChainSpec chainSpec = ctx.Resolve<ChainSpec>();
                IDiscoveryConfig discoveryConfig = ctx.Resolve<IDiscoveryConfig>();

                // Was in `UpdateDiscoveryConfig` step.
                if (discoveryConfig.Bootnodes != string.Empty)
                {
                    if (chainSpec.Bootnodes.Length != 0)
                    {
                        discoveryConfig.Bootnodes += "," + string.Join(",", chainSpec.Bootnodes.Select(static bn => bn.ToString()));
                    }
                }
                else if (chainSpec.Bootnodes is not null)
                {
                    discoveryConfig.Bootnodes = string.Join(",", chainSpec.Bootnodes.Select(static bn => bn.ToString()));
                }

                if (networkConfig.DiscoveryDns == null)
                {
                    string chainName = BlockchainIds.GetBlockchainName(chainSpec!.NetworkId).ToLowerInvariant();
                    networkConfig.DiscoveryDns = $"all.{chainName}.ethdisco.net";
                }
                networkConfig.Bootnodes = discoveryConfig.Bootnodes;
                return networkConfig;
            })
            ;


        // Discovery app
        // Needed regardless if used or not because of StopAsync in runner.
        // The DiscV4/5 is in CompositeDiscoveryApp.
        if (!initConfig.DiscoveryEnabled)
            builder.AddSingleton<IDiscoveryApp, NullDiscoveryApp>();
        else
            builder.AddSingleton<IDiscoveryApp, CompositeDiscoveryApp>();

        if (!networkConfig.OnlyStaticPeers)
        {
            // These are INodeSource only if `OnlyStaticPeers` is false.
            builder
                .Bind<INodeSource, IDiscoveryApp>()
                .Bind<INodeSource, EnrDiscovery>();
        }
    }
}
