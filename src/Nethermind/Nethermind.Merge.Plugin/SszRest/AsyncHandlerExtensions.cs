// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest;

internal static class AsyncHandlerExtensions
{
    public static Func<TRequest, Task<ResultWrapper<TResult?>>> AsFunc<TRequest, TResult>(
        this IAsyncHandler<TRequest, TResult?> handler) where TResult : class
        => handler.HandleAsync;
}
