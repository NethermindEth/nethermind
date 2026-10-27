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
        private static readonly AsyncLocal<FlowTimeout?> _flowTimeout = new();

        /// <summary>
        /// Hands <paramref name="cts"/> to the next <see cref="BuildTimeoutCancellationToken"/> in the current async flow,
        /// and keeps it out of the shared pool when <see cref="ReturnTimeoutCancellationToken"/> gets it back.
        /// </summary>
        /// <remarks>
        /// Lets tests observe and cancel the source a request rents. Passing it through the shared pool instead would let
        /// a request of any concurrently running test take it.
        /// </remarks>
        internal static void UseForNextTimeoutInFlow(CancellationTokenSource cts) => _flowTimeout.Value = new FlowTimeout(cts);

        /// <summary>Ends <see cref="UseForNextTimeoutInFlow"/> in the current async flow.</summary>
        /// <returns>Whether <paramref name="cts"/> was rented and then returned in a reusable state.</returns>
        internal static bool EndTimeoutInFlow(CancellationTokenSource cts)
        {
            FlowTimeout? flow = _flowTimeout.Value;
            _flowTimeout.Value = null;
            return flow is not null && ReferenceEquals(flow.Source, cts) && Volatile.Read(ref flow.State) == FlowTimeout.Returned;
        }

        /// <summary>The size of the debug module pool: <see cref="IJsonRpcConfig.DebugModuleConcurrentInstances"/>, or the processor count up to 16.</summary>
        public static int GetDebugModuleConcurrentInstances(this IJsonRpcConfig config) =>
            config.DebugModuleConcurrentInstances ?? Math.Min(Environment.ProcessorCount, 16);

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
            // The flows forked after the plant share it, so the exchange hands the source out only once.
            if (_flowTimeout.Value is { } flow
                && Interlocked.CompareExchange(ref flow.State, FlowTimeout.Rented, FlowTimeout.Planted) == FlowTimeout.Planted)
            {
                flow.Source.CancelAfter(config.Timeout);
                return flow.Source;
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
                if (_flowTimeout.Value is { } flow && ReferenceEquals(flow.Source, cts))
                {
                    Volatile.Write(ref flow.State, FlowTimeout.Returned);
                    return;
                }

                if (Interlocked.Increment(ref _ctsPoolSize) <= MaxPoolSize)
                {
                    _ctsPool.Enqueue(cts);
                    return;
                }

                Interlocked.Decrement(ref _ctsPoolSize);
            }

            cts.Dispose();
        }

        private sealed class FlowTimeout(CancellationTokenSource source)
        {
            public const int Planted = 0;
            public const int Rented = 1;
            public const int Returned = 2;

            public readonly CancellationTokenSource Source = source;
            public int State = Planted;
        }
    }
}
