// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;

namespace Nethermind.JsonRpc.Modules;

public static class IContainerBuilderExtensions
{
    public static ContainerBuilder RegisterSingletonJsonRpcModule<T, TImpl>(this ContainerBuilder builder) where T : IRpcModule where TImpl : T
    {
        return builder
            .AddSingleton<T, TImpl>()
            .AddSingleton<RpcModuleInfo>((ctx) =>
            {
                T instance = ctx.Resolve<T>();
                return new RpcModuleInfo(typeof(T), new SingletonModulePool<T>(instance, true));
            });
    }
}
