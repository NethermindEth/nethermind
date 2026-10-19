// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private static readonly AsyncLocal<StrongBox<CancellationTokenSource?>?> _nextTimeoutInFlow = new();

        /// <summary>
        /// Hands <paramref name="cts"/> to the next <see cref="BuildTimeoutCancellationToken"/> in the current async flow;
        /// <see langword="null"/> withdraws a source that was not taken.
        /// </summary>
        /// <remarks>
        /// Lets tests observe and cancel the source a request rents. Queueing it in the shared pool instead would let
        /// a request of any concurrently running test take it.
        /// </remarks>
        internal static void UseForNextTimeoutInFlow(CancellationTokenSource? cts) =>
            _nextTimeoutInFlow.Value = cts is null ? null : new StrongBox<CancellationTokenSource?>(cts);

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
            // The box is shared by the flows forked after it was set, so clearing it hands the source out only once.
            if (_nextTimeoutInFlow.Value is { Value: { } planted } next)
            {
                next.Value = null;
                planted.CancelAfter(config.Timeout);
                return planted;
            }

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
        /// Rents a timeout source like <see cref="BuildTimeoutCancellationToken"/> for a caller whose token does not outlive the lease.
        /// </summary>
        internal static RentedTimeout RentTimeout(this IJsonRpcConfig config) => new(config.BuildTimeoutCancellationToken());

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
