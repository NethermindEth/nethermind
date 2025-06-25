// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;

namespace Nethermind.JsonRpc.Modules;

/// <summary>
/// Automatically create IRpcModule factory for <see cref="T"/> by creating a lifetime scope for each
/// instance. Don't forget to add <see cref="T"/> as a scoped component also!
/// </summary>
/// <param name="rootScope"></param>
/// <typeparam name="T"></typeparam>
public class AutoRpcModuleFactory<T>(ILifetimeScope rootScope) : IRpcModuleFactory<T> where T : IRpcModule
{
    public T Create()
    {
        ILifetimeScope childScope = rootScope.BeginLifetimeScope();
        rootScope.Disposer.AddInstanceForAsyncDisposal(childScope);

        return childScope.Resolve<T>();
    }
}
