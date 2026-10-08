// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using System.Threading;

namespace Nethermind.JsonRpc.Modules;

public class LazyModulePool<T>(Lazy<IRpcModulePool<T>> lazyBasePool) : IRpcModulePool<T>, IExclusiveRpcModulePool where T : IRpcModule
{
    private IRpcModulePool<T> BasePool => lazyBasePool.Value;

    internal bool SupportsExclusiveRental { get; init; }
    bool IExclusiveRpcModulePool.SupportsExclusiveRental => SupportsExclusiveRental;

    ValueTask<IRpcModule> IExclusiveRpcModulePool.RentExclusive(CancellationToken cancellationToken) =>
        SupportsExclusiveRental && BasePool is IExclusiveRpcModulePool { SupportsExclusiveRental: true } pool
            ? pool.RentExclusive(cancellationToken)
            : throw new NotSupportedException("This module pool does not support cancellable exclusive rentals.");

    public Task<T> GetModule(bool canBeShared) => BasePool.GetModule(canBeShared);

    public void ReturnModule(T module) => BasePool.ReturnModule(module);

    public IRpcModuleFactory<T> Factory => BasePool.Factory;

    public void Preload() => BasePool.Preload();
}
