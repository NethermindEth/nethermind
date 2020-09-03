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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.BeaconNode.Services;
using Nethermind.Core2;

namespace Nethermind.BeaconNode
{
    public static class BeaconNodeServiceCollectionExtensions
    {
        public static void AddBeaconNode(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<INodeStart, NodeStart>();
            services.AddSingleton<GenesisChainStart>();
            services.AddSingleton<IEth1Genesis>(provider => provider.GetRequiredService<GenesisChainStart>());
            services.AddSingleton<IBeaconChainUtility, BeaconChainUtility>();
            services.AddSingleton<IDepositStore, DepositStore>();
            services.AddSingleton<BeaconStateAccessor>();
            services.AddSingleton<BeaconStateTransition>();
            services.AddSingleton<BeaconStateMutator>();
            services.AddSingleton<IForkChoice, ForkChoice>();
            services.AddSingleton<ValidatorAssignments>();
            services.AddSingleton<ValidatorAssignmentsCache>();
            services.AddSingleton<BlockProducer>();
            services.AddSingleton<AttestationProducer>();
            services.AddSingleton<ISynchronizationManager, SynchronizationManager>();
            services.AddSingleton<IBeaconNodeApi, BeaconNodeFacade>();

            services.AddHostedService<BeaconNodeWorker>();
        }
    }
}