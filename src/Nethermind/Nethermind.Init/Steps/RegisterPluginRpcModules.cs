// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Evm;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore), typeof(InitializeBlockchain), typeof(InitializePlugins), typeof(InitializeBlockProducer), typeof(RegisterRpcModules))]
public class RegisterPluginRpcModules : IStep
{
    private readonly INethermindApi _api;

    public RegisterPluginRpcModules(INethermindApi api)
    {
        _api = api;
    }

    public virtual async Task Execute(CancellationToken cancellationToken)
    {
        IRpcModuleProvider rpcModuleProvider = _api.RpcModuleProvider!;

        foreach (INethermindPlugin plugin in _api.Plugins)
        {
            await plugin.InitRpcModules();
        }

        EvmRpcModule evmRpcModule = new(_api.ManualBlockProductionTrigger);
        rpcModuleProvider.RegisterSingle<IEvmRpcModule>(evmRpcModule);
    }
}
