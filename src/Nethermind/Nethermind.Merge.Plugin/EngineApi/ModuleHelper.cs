// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.EngineApi;

internal static class ModuleHelper
{
    public static async Task<ResultWrapper<TResult>> RunHandler<TRequest, TResult>(
        IAsyncHandler<TRequest, TResult> handler,
        TRequest request,
        Action<long> updateMetrics,
        Locker locker,
        ILogger logger,
        [System.Runtime.CompilerServices.CallerMemberName] string methodName = "")
    {
        if (await locker.WaitAsync())
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                return await handler.HandleAsync(request);
            }
            catch (Exception exception)
            {
                if (logger.IsError) logger.Error($"{methodName} failed: {exception}");
                return ResultWrapper<TResult>.Fail(exception.Message);
            }
            finally
            {
                updateMetrics(watch.ElapsedMilliseconds);
                locker.Release();
            }
        }
        else
        {
            if (logger.IsWarn) logger.Warn($"{methodName} timed out");
            return ResultWrapper<TResult>.Fail("Timed out", ErrorCodes.Timeout);
        }
    }
}
