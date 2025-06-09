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

public class AutoOptimismTraceModuleFactory(IWorldStateManager worldStateManager, Func<ICodeInfoRepository> codeInfoRepositoryFunc, ILifetimeScope rootLifetimeScope) : AutoTraceModuleFactory(worldStateManager, codeInfoRepositoryFunc, rootLifetimeScope)
{
    protected override ContainerBuilder ConfigureCommonBlockProcessing(
        ContainerBuilder builder,
        ICodeInfoRepository codeInfoRepository,
        IWorldState worldState,
        string transactionExecutorName
    )
    {
        return base.ConfigureCommonBlockProcessing(builder, codeInfoRepository, worldState, transactionExecutorName)
            .AddScoped<IWithdrawalProcessor>(new BlockProductionWithdrawalProcessor(new NullWithdrawalProcessor())); // Why? Is this global all the time
    }
}
