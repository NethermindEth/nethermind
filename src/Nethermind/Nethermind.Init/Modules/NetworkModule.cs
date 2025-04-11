// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats;

namespace Nethermind.Init.Modules;

public class NetworkModule(IConfigProvider configProvider): Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder
            .AddModule(new SynchronizerModule(configProvider.GetConfig<ISyncConfig>()))

            .AddSingleton<INodeStatsManager>((ctx) => new NodeStatsManager(
                ctx.Resolve<ITimerFactory>(),
                ctx.Resolve<ILogManager>(),
                ctx.Resolve<INetworkConfig>()
                    .MaxCandidatePeerCount)) // The INetworkConfig is not referable in NodeStatsManager.

            ;
    }
}
