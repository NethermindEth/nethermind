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
            .RegisterSingletonJsonRpcModule<T>();
    }

    public static ContainerBuilder RegisterSingletonJsonRpcModule<T>(this ContainerBuilder builder) where T : IRpcModule
    {
        return builder
            .AddSingleton<RpcModuleInfo>((ctx) =>
            {
                T instance = ctx.Resolve<T>();
                return new RpcModuleInfo(typeof(T), new SingletonModulePool<T>(instance, true));
            });
    }

    public static ContainerBuilder RegisterBoundedJsonRpcModule<T, TFactory>(
        this ContainerBuilder builder,
        int maxCount,
        int timeout)
        where T : IRpcModule
        where TFactory : IRpcModuleFactory<T>
    {
        return builder
            .AddSingleton<TFactory>()
            .AddSingleton<RpcModuleInfo>((ctx) =>
            {
                TFactory factory = ctx.Resolve<TFactory>();
                return new RpcModuleInfo(typeof(T), new BoundedModulePool<T>(factory, maxCount, timeout));
            });
    }
}
