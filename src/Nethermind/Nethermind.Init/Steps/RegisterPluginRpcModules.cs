// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Evm;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore), typeof(InitializeBlockchain), typeof(InitializePlugins), typeof(InitializeBlockProducer), typeof(RegisterRpcModules))]
public class RegisterPluginRpcModules(
    IRpcModuleProvider rpcModuleProvider,
    IInitConfig initConfig,
    IBlockProcessingQueue blockProcessingQueue,
    INethermindPlugin[] plugins,
    IManualBlockProductionTrigger manualBlockProductionTrigger
) : IStep
{
    public virtual async Task Execute(CancellationToken cancellationToken)
    {
        if (!initConfig.InRunnerTest && !initConfig.EnableRpcDuringSyncStartup)
        {
            // Wait for block processing queue to be empty before starting RPC modules to prevent
            // engine API messages before end of processing of all blocks after restart.
            // This can be bypassed by setting EnableRpcDuringSyncStartup to true.
            while (!blockProcessingQueue!.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(100, cancellationToken);
            }
            await Task.Delay(5000, cancellationToken);
        }

        foreach (INethermindPlugin plugin in plugins)
        {
            await plugin.InitRpcModules();
        }

        EvmRpcModule evmRpcModule = new(manualBlockProductionTrigger);
        rpcModuleProvider.RegisterSingle<IEvmRpcModule>(evmRpcModule);
    }
}
