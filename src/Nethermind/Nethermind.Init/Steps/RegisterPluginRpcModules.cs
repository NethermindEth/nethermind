// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core;
using Nethermind.Init.Steps.Migrations;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Evm;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.JsonRpc.Modules.Witness;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.JsonRpc.Modules.Rpc;

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
