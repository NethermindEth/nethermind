// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Consensus.Producers;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Evm;
using Nethermind.Core.Events;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(SetupKeyStore), typeof(InitializeBlockchain), typeof(InitializePlugins), typeof(InitializeBlockProducer), typeof(RegisterRpcModules))]
public class RegisterPluginRpcModules(
    IRpcModuleProvider rpcModuleProvider,
    INethermindPlugin[] plugins,
    IManualBlockProductionTrigger manualBlockProductionTrigger
) : IStep
{
    public virtual async Task Execute(CancellationToken cancellationToken)
    {
        foreach (INethermindPlugin plugin in plugins)
        {
            await plugin.InitRpcModules();
        }

        EvmRpcModule evmRpcModule = new(manualBlockProductionTrigger);
        rpcModuleProvider.RegisterSingle<IEvmRpcModule>(evmRpcModule);
    }
}
