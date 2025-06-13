// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.State;

namespace Nethermind.Optimism.Rpc;

public class OptimismTraceModuleFactory(IWorldStateManager worldStateManager, Func<ICodeInfoRepository> codeInfoRepositoryFunc, ILifetimeScope rootLifetimeScope) : TraceModuleFactory(worldStateManager, codeInfoRepositoryFunc, rootLifetimeScope)
{
    protected override ContainerBuilder ConfigureCommonBlockProcessing<T>(ContainerBuilder builder)
    {
        return base.ConfigureCommonBlockProcessing<T>(builder)
            .AddScoped<IWithdrawalProcessor>(new BlockProductionWithdrawalProcessor(new NullWithdrawalProcessor())); // Why? Is this global all the time
    }
}
