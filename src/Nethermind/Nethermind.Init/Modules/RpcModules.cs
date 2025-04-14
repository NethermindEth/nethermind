// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Init.Modules;

public class RpcModules: Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .Add<IRpcModuleProvider, RpcModuleProvider>()
            ;
    }
}
