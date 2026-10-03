// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.JsonRpc
{
    public static class JsonRpcConfigExtension
    {
        private static readonly ConcurrentQueue<CancellationTokenSource> _ctsPool = new();
        private const int MaxPoolSize = 64;
        private static int _ctsPoolSize;

        /// <summary>
        /// The number of EVM-executing requests (<see cref="Modules.JsonRpcMethodAttribute.IsEvmExecution"/>) any caller may run
        /// at once: <see cref="IJsonRpcConfig.EthModuleConcurrentInstances"/>, and at least one.
        /// </summary>
        public static int GetEvmExecutionSlots(this IJsonRpcConfig config) =>
            Math.Max(1, config.EthModuleConcurrentInstances ?? Environment.ProcessorCount);

        /// <summary>
        /// The most EVM-executing requests that run at once: <see cref="GetEvmExecutionSlots"/> plus one that only authenticated
        /// and IPC callers may take. Every pool of environments those requests execute in must hold this many.
        /// </summary>
        public static int GetMaxConcurrentEvmExecutions(this IJsonRpcConfig config) => config.GetEvmExecutionSlots() + 1;

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

            if (_ctsPool.TryDequeue(out CancellationTokenSource? cts))
            {
                Interlocked.Decrement(ref _ctsPoolSize);
                cts.CancelAfter(config.Timeout);
                return cts;
            }

            return new CancellationTokenSource(config.Timeout);
        }

        /// <summary>
        /// Returns a CTS to the pool if it can be reset, otherwise disposes it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReturnTimeoutCancellationToken(CancellationTokenSource cts)
        {
            if (cts.TryReset())
            {
                if (Interlocked.Increment(ref _ctsPoolSize) <= MaxPoolSize)
                {
                    _ctsPool.Enqueue(cts);
                    return;
                }

                Interlocked.Decrement(ref _ctsPoolSize);
            }

            cts.Dispose();
        }
    }
}
