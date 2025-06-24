// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
                return new RpcModuleInfo(typeof(T), new LazyModulePool<T>(new Lazy<IRpcModulePool<T>>(() =>
                {
                    T instance = ctx.Resolve<T>();
                    return new SingletonModulePool<T>(instance, true);
                })));
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
            .AddSingleton<IRpcModuleFactory<T>, TFactory>()
            .AddSingleton<RpcModuleInfo>((ctx) =>
            {
                return new RpcModuleInfo(typeof(T), new LazyModulePool<T>(new Lazy<IRpcModulePool<T>>(() =>
                {
                    IRpcModuleFactory<T> factory = ctx.Resolve<IRpcModuleFactory<T>>();
                    return new BoundedModulePool<T>(factory, maxCount, timeout);
                })));
            });
    }
}
