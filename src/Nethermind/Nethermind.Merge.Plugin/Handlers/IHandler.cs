// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Handlers
{
    /// <summary>
    /// Handles a parameterized JSON RPC request.
    /// </summary>
    /// <typeparam name="TRequest">Request parameters type</typeparam>
    /// <typeparam name="TResult">Request result type</typeparam>
    public interface IHandler<in TRequest, TResult>
    {
        ResultWrapper<TResult> Handle(TRequest request);
    }

    /// <summary>
    /// Handles a parameterless JSON RPC request.
    /// </summary>
    /// <typeparam name="TResult">Request result type</typeparam>
    public interface IHandler<TResult>
    {
        ResultWrapper<TResult> Handle();
    }
}
