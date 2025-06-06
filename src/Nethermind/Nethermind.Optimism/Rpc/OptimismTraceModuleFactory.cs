// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.State;

namespace Nethermind.Optimism.Rpc;

public class AutoOptimismTraceModuleFactory(IWorldStateManager worldStateManager, ILifetimeScope rootLifetimeScope) : AutoTraceModuleFactory(worldStateManager, rootLifetimeScope)
{
    protected override ContainerBuilder ConfigureCommonBlockProcessing(ContainerBuilder builder)
    {
        return base.ConfigureCommonBlockProcessing(builder)
            .AddScoped<IWithdrawalProcessor>(new BlockProductionWithdrawalProcessor(new NullWithdrawalProcessor())); // Why? Is this global all the time
    }
}
