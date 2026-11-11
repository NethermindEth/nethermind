// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules
{
    public interface IRpcModulePool
    {
        void Preload() { }
    }

    public interface IRpcModulePool<T> : IRpcModulePool where T : IRpcModule
    {
        Task<T> GetModule(bool canBeShared);

        /// <summary>Rents a module; cancelling <paramref name="cancellationToken"/> abandons a rental that is still queued.</summary>
        /// <remarks>
        /// An abandoned rental throws an <see cref="System.OperationCanceledException"/> for this token and frees its place
        /// in the queue. A rental the pool can grant at once is not affected. The default implementation ignores the token,
        /// so a pool that does not override it keeps its old behaviour.
        /// </remarks>
        Task<T> GetModule(bool canBeShared, CancellationToken cancellationToken) => GetModule(canBeShared);

        void ReturnModule(T module);

        IRpcModuleFactory<T> Factory { get; }
    }
}
