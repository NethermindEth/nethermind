// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules;

public class LazyModulePool<T>(Lazy<IRpcModulePool<T>> lazyBasePool) : IRpcModulePool<T> where T : IRpcModule
{
    private IRpcModulePool<T> BasePool => lazyBasePool.Value;

    public Task<T> GetModule(bool canBeShared) => BasePool.GetModule(canBeShared);

    public Task<T> GetModule(bool canBeShared, CancellationToken cancellationToken) => BasePool.GetModule(canBeShared, cancellationToken);

    public void ReturnModule(T module) => BasePool.ReturnModule(module);

    public IRpcModuleFactory<T> Factory => BasePool.Factory;

    public void Preload() => BasePool.Preload();
}
