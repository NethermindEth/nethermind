// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;

namespace Nethermind.Core.Test.Modules;

public class PseudoNetworkModule() : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IGossipPolicy>(Policy.FullGossip)

            // Snap capability is contributed by SnapP2PCapabilityResolver, registered in the production NetworkModule.

            // Some config migration
            .AddDecorator<INetworkConfig>((ctx, networkConfig) =>
            {
                ILogManager logManager = ctx.Resolve<ILogManager>();
                if (networkConfig.DiagTracerEnabled)
                {
                    NetworkDiagTracer.IsEnabled = true;
                }
                if (NetworkDiagTracer.IsEnabled)
                {
                    NetworkDiagTracer.Start(logManager);
                }
                int maxPeersCount = networkConfig.ActivePeersMaxCount;
                Network.Metrics.PeerLimit = maxPeersCount;
                return networkConfig;
            })
            ;
    }

}
