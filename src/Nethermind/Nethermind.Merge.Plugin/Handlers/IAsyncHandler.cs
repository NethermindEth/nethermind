// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Handlers
{
    /// <summary>
    /// Handles a parameterized JSON RPC request asynchronously.
    /// </summary>
    /// <typeparam name="TRequest">Request parameters type</typeparam>
    /// <typeparam name="TResult">Request result type</typeparam>
    public interface IAsyncHandler<in TRequest, TResult>
    {
        Task<ResultWrapper<TResult>> HandleAsync(TRequest request);
    }
}
