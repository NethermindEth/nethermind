// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Evm;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(SetupKeyStore), typeof(InitializeBlockchain), typeof(InitializePlugins), typeof(InitializeBlockProducer), typeof(RegisterRpcModules))]
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
        if (!initConfig.InRunnerTest)
        {
            // Ugly temporary hack to not receive engine API messages before end of processing of all blocks after restart.
            // Then we will wait 5s more to ensure everything is processed
            while (!blockProcessingQueue.IsEmpty && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
                // If we have new blocks coming from the network we will spin in this loop until we sync head
            }

            await Task.Delay(5000, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        foreach (INethermindPlugin plugin in plugins)
        {
            await plugin.InitRpcModules();
        }

        EvmRpcModule evmRpcModule = new(manualBlockProductionTrigger);
        rpcModuleProvider.RegisterSingle<IEvmRpcModule>(evmRpcModule);
    }
}
