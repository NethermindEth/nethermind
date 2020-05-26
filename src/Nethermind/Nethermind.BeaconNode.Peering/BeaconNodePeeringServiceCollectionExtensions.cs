//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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