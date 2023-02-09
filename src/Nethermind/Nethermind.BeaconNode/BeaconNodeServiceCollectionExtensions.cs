// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
