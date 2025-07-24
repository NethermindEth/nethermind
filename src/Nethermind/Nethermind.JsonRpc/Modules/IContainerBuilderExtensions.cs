// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;

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
            .AddLast<RpcModuleInfo>((ctx) =>
            {
                Lazy<T> instance = ctx.Resolve<Lazy<T>>();
                return new RpcModuleInfo(typeof(T), new LazyModulePool<T>(new Lazy<IRpcModulePool<T>>(() =>
                {
                    return new SingletonModulePool<T>(instance.Value, true);
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
            .AddLast<RpcModuleInfo>((ctx) =>
            {
                Lazy<IRpcModuleFactory<T>> factory = ctx.Resolve<Lazy<IRpcModuleFactory<T>>>();
                return new RpcModuleInfo(typeof(T), new LazyModulePool<T>(new Lazy<IRpcModulePool<T>>(() =>
                {
                    return new BoundedModulePool<T>(factory.Value, maxCount, timeout);
                })));
            });
    }
}
