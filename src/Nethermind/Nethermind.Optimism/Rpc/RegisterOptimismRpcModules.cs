// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.Optimism;

public class RegisterOptimismRpcModules : RegisterRpcModules
{
    private readonly INethermindApi _api;

    public RegisterOptimismRpcModules(INethermindApi api) : base(api)
    {
        _api = api;
    }

    protected override ModuleFactoryBase<IEthRpcModule> CreateEthModuleFactory()
    {
        ModuleFactoryBase<IEthRpcModule> ethModuleFactory = base.CreateEthModuleFactory();

        return new OptimismEthModuleFactory(ethModuleFactory);
    }
}
