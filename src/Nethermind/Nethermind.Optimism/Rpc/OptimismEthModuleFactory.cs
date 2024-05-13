// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.Optimism;

public class OptimismEthModuleFactory : ModuleFactoryBase<IEthRpcModule>
{
    private readonly ModuleFactoryBase<IEthRpcModule> _ethModuleFactory;

    public OptimismEthModuleFactory(ModuleFactoryBase<IEthRpcModule> ethModuleFactory)
    {
        _ethModuleFactory = ethModuleFactory;
    }

    public override IEthRpcModule Create()
    {
        return new OptimismEthRpcModule(_ethModuleFactory.Create());
    }
}
