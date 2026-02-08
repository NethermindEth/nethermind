// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Nethermind.JsonRpc
{
    public static class JsonRpcConfigExtension
    {
        private static readonly ConcurrentBag<CancellationTokenSource> _ctsPool = new();
        private const int MaxPoolSize = 64;

        public static void EnableModules(this IJsonRpcConfig config, params string[] modules)
        {
            HashSet<string> enabledModules = config.EnabledModules.ToHashSet();
            for (int i = 0; i < modules.Length; i++)
            {
                enabledModules.Add(modules[i]);
            }
            config.EnabledModules = enabledModules.ToArray();
        }

        /// <summary>
        /// Rents a <see cref="CancellationTokenSource"/> that timeouts after <see cref="IJsonRpcConfig.Timeout"/>.
        /// When debugger is attached, no timeout is applied.
        /// Call <see cref="ReturnTimeoutCancellationToken"/> to return to pool.
        /// </summary>
        public static CancellationTokenSource BuildTimeoutCancellationToken(this IJsonRpcConfig config)
        {
            if (Debugger.IsAttached)
            {
                return new CancellationTokenSource();
            }

            if (_ctsPool.TryTake(out CancellationTokenSource? cts))
            {
                cts.CancelAfter(config.Timeout);
                return cts;
            }

            return new CancellationTokenSource(config.Timeout);
        }

        /// <summary>
        /// Returns a CTS to the pool if it can be reset, otherwise disposes it.
        /// </summary>
        public static void ReturnTimeoutCancellationToken(CancellationTokenSource cts)
        {
            if (cts.TryReset() && _ctsPool.Count < MaxPoolSize)
            {
                _ctsPool.Add(cts);
            }
            else
            {
                cts.Dispose();
            }
        }
    }
}
