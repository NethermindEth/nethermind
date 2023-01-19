// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Peering.Mothra;

namespace Nethermind.BeaconNode.Peering
{
    public static class BeaconNodePeeringServiceCollectionExtensions
    {
        public static void AddBeaconNodePeering(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<NetworkingConfiguration>(x =>
                configuration.Bind("BeaconChain:NetworkingConfiguration", x));

            if (configuration.GetSection("Peering:Mothra").Exists())
            {
                services.Configure<MothraConfiguration>(x => configuration.Bind("Peering:Mothra", x));
                services.AddSingleton<PeerManager>();
                services.AddSingleton<INetworkPeering, MothraNetworkPeering>();
                services.AddHostedService<MothraPeeringWorker>();
                services.AddSingleton<PeerDiscoveredProcessor>();
                services.AddSingleton<RpcPeeringStatusProcessor>();
                services.AddSingleton<RpcBeaconBlocksByRangeProcessor>();
                services.AddSingleton<SignedBeaconBlockProcessor>();
                services.AddSingleton<IMothraLibp2p, MothraLibp2p>();
                services.TryAddTransient<IFileSystem, FileSystem>();
            }
            else
            {
                // TODO: Maybe add offline testing support with a null networking interface
                throw new Exception("No peering configuration found.");
            }
        }
    }
}
