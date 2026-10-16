// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;

namespace Nethermind.JsonRpc
{
    public static class Metrics
    {
        [CounterMetric]
        [Description("Total number of JSON RPC requests received by the node.")]
        public static long JsonRpcRequests { get; set; }

        [CounterMetric]
        [Description("Number of JSON RPC requests that failed JSON deserialization.")]
        public static long JsonRpcRequestDeserializationFailures { get; set; }

        [CounterMetric]
        [Description("Number of JSON RPC requests that were invalid.")]
        public static long JsonRpcInvalidRequests { get; set; }

        [CounterMetric]
        [Description("Number of JSON RPC requests rejected or timed out at the EVM-execution admission gate (RpcAdmissionQueueFullRejections, RpcAdmissionNotQueueableRejections and RpcAdmissionWaitTimeoutRejections) or at a module, shared-request, execution-environment, or synchronous-transaction concurrency limit.")]
        public static long JsonRpcOverloadRejections => _jsonRpcOverloadRejections;
        private static long _jsonRpcOverloadRejections;
        internal static void IncrementJsonRpcOverloadRejections() => Interlocked.Increment(ref _jsonRpcOverloadRejections);

        /// <summary>Number of gated EVM-executing JSON-RPC requests waiting for an execution slot.</summary>
        [GaugeMetric]
        [Description("Number of gated JSON RPC requests (eth_simulateV1, and eth_call, eth_estimateGas or eth_createAccessList with a state or block override) waiting for an execution slot.")]
        public static long RpcAdmissionQueued => _rpcAdmissionQueued;
        private static long _rpcAdmissionQueued;
        internal static void ChangeRpcAdmissionQueued(long delta) => Interlocked.Add(ref _rpcAdmissionQueued, delta);

        /// <summary>Number of gated EVM-executing JSON-RPC requests holding an execution slot.</summary>
        [GaugeMetric]
        [Description("Number of gated JSON RPC requests (eth_simulateV1, and eth_call, eth_estimateGas or eth_createAccessList with a state or block override) holding an execution slot.")]
        public static long RpcAdmissionInFlight => _rpcAdmissionInFlight;
        private static long _rpcAdmissionInFlight;
        internal static void ChangeRpcAdmissionInFlight(long delta) => Interlocked.Add(ref _rpcAdmissionInFlight, delta);

        /// <summary>Number of gated EVM-executing JSON-RPC requests rejected at once because the queue was full.</summary>
        [CounterMetric]
        [Description("Number of gated EVM-executing JSON RPC requests rejected without queueing because JsonRpc.EvmExecutionQueueLimit requests were already waiting.")]
        public static long RpcAdmissionQueueFullRejections => _rpcAdmissionQueueFullRejections;
        private static long _rpcAdmissionQueueFullRejections;
        internal static void IncrementRpcAdmissionQueueFullRejections() => Interlocked.Increment(ref _rpcAdmissionQueueFullRejections);

        /// <summary>Number of gated EVM-executing JSON-RPC requests rejected at once because they may not queue.</summary>
        [CounterMetric]
        [Description("Number of gated EVM-executing JSON RPC requests rejected because every execution slot was busy and the request may not queue: a batch item whose batch has used up the wait budget, or any request when queueing is disabled.")]
        public static long RpcAdmissionNotQueueableRejections => _rpcAdmissionNotQueueableRejections;
        private static long _rpcAdmissionNotQueueableRejections;
        internal static void IncrementRpcAdmissionNotQueueableRejections() => Interlocked.Increment(ref _rpcAdmissionNotQueueableRejections);

        /// <summary>Number of gated EVM-executing JSON-RPC requests rejected after waiting their whole budget.</summary>
        [CounterMetric]
        [Description("Number of gated EVM-executing JSON RPC requests rejected after waiting JsonRpc.EvmExecutionMaxQueueWaitMs, or what was left of it for a batch item, for an execution slot.")]
        public static long RpcAdmissionWaitTimeoutRejections => _rpcAdmissionWaitTimeoutRejections;
        private static long _rpcAdmissionWaitTimeoutRejections;
        internal static void IncrementRpcAdmissionWaitTimeoutRejections() => Interlocked.Increment(ref _rpcAdmissionWaitTimeoutRejections);

        /// <summary>Number of queued gated EVM-executing JSON-RPC requests whose caller went away before getting a slot.</summary>
        [CounterMetric]
        [Description("Number of queued gated EVM-executing JSON RPC requests whose caller disconnected before getting an execution slot.")]
        public static long RpcAdmissionCancellations => _rpcAdmissionCancellations;
        private static long _rpcAdmissionCancellations;
        internal static void IncrementRpcAdmissionCancellations() => Interlocked.Increment(ref _rpcAdmissionCancellations);

        /// <summary>Number of gated EVM-executing JSON-RPC requests granted an execution slot after waiting for one.</summary>
        [CounterMetric]
        [Description("Number of gated EVM-executing JSON RPC requests granted an execution slot after waiting in the queue.")]
        public static long RpcAdmissionQueuedGrants => _rpcAdmissionQueuedGrants;
        private static long _rpcAdmissionQueuedGrants;

        /// <summary>Total time, in microseconds, that the requests counted in <see cref="RpcAdmissionQueuedGrants"/> waited.</summary>
        [CounterMetric]
        [Description("Total time, in microseconds, that the requests counted in RpcAdmissionQueuedGrants waited for an execution slot.")]
        public static long RpcAdmissionQueueWaitMicroseconds => _rpcAdmissionQueueWaitMicroseconds;
        private static long _rpcAdmissionQueueWaitMicroseconds;

        internal static void AddRpcAdmissionQueuedGrant(long waitedMicroseconds)
        {
            Interlocked.Increment(ref _rpcAdmissionQueuedGrants);
            Interlocked.Add(ref _rpcAdmissionQueueWaitMicroseconds, waitedMicroseconds);
        }

        [CounterMetric]
        [Description("Number of JSON RPC requests processed with errors.")]
        public static long JsonRpcErrors { get; set; }

        [CounterMetric]
        [Description("Number of JSON RPC requests processed successfully.")]
        public static long JsonRpcSuccesses { get; set; }

        [CounterMetric]
        [Description("Number of JSON RPC bytes sent.")]
        public static long JsonRpcBytesSent => JsonRpcBytesSentHttp + JsonRpcBytesSentWebSockets + JsonRpcBytesSentIpc;

        [CounterMetric]
        [Description("Number of JSON RPC bytes sent through http.")]
        public static long JsonRpcBytesSentHttp;

        [CounterMetric]
        [Description("Number of JSON RPC bytes sent through web sockets.")]
        public static long JsonRpcBytesSentWebSockets;

        [CounterMetric]
        [Description("Number of JSON RPC bytes sent through IPC.")]
        public static long JsonRpcBytesSentIpc;

        [CounterMetric]
        [Description("Number of JSON RPC bytes received.")]
        public static long JsonRpcBytesReceived => JsonRpcBytesReceivedHttp + JsonRpcBytesReceivedWebSockets + JsonRpcBytesReceivedIpc;

        [CounterMetric]
        [Description("Number of JSON RPC bytes received through http.")]
        public static long JsonRpcBytesReceivedHttp;

        [CounterMetric]
        [Description("Number of JSON RPC bytes received through web sockets.")]
        public static long JsonRpcBytesReceivedWebSockets;

        [CounterMetric]
        [Description("Number of JSON RPC bytes received through IPC.")]
        public static long JsonRpcBytesReceivedIpc;

        [HistogramMetric(
            LabelNames = ["method", "status"],
            Buckets = [10, 50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000, 25_000, 50_000, 100_000, 250_000, 500_000, 1_000_000])]
        [Description("Individual rpc call duration metric calls (microseconds)")]
        public static IMetricObserver JsonRpcCallDurationMicros = NoopMetricObserver.Instance;
    }

    /// <summary>The method and status labels of <see cref="Metrics.JsonRpcCallDurationMicros"/>.</summary>
    /// <remarks>
    /// Instances are cached per method for the process lifetime, which stays bounded because calls are reported
    /// under resolved method names or <see cref="RpcReport.UnknownMethod"/>.
    /// </remarks>
    internal sealed class JsonRpcMetricLabels : IStableMetricLabels
    {
        private static readonly ConcurrentDictionary<string, (JsonRpcMetricLabels Success, JsonRpcMetricLabels Fail)> _cache = new(StringComparer.Ordinal);

        private JsonRpcMetricLabels(string method, string status) => Labels = [method, status];

        public string[] Labels { get; }

        public static JsonRpcMetricLabels Get(string method, bool success)
        {
            (JsonRpcMetricLabels Success, JsonRpcMetricLabels Fail) labels =
                _cache.GetOrAdd(method, static m => (new JsonRpcMetricLabels(m, "success"), new JsonRpcMetricLabels(m, "fail")));
            return success ? labels.Success : labels.Fail;
        }
    }
}
