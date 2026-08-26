// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;
using Nethermind.JsonRpc.Modules;

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
        [Description("Number of JSON RPC requests rejected or timed out at a concurrency cap: the per-cost-class admission gate (see the RpcAdmission* metrics and JsonRpc.EvmExecutionConcurrency / TracingConcurrency / ProofConcurrency / MaxQueueWaitMs), the module pool, or the override-environment limit. A nonzero rate means callers receive 'Too many requests'.")]
        public static long JsonRpcOverloadRejections => _jsonRpcOverloadRejections;
        private static long _jsonRpcOverloadRejections;
        internal static void IncrementJsonRpcOverloadRejections() => Interlocked.Increment(ref _jsonRpcOverloadRejections);

        [CounterMetric]
        [Description("Number of gated JSON RPC requests shed up front because the predicted queue wait exceeded JsonRpc.MaxQueueWaitMs, per cost class.")]
        [KeyIsLabel("cost_class")]
        public static ConcurrentDictionary<RpcMethodCostClass, long> RpcAdmissionPredictedWaitRejections { get; } = new();

        [CounterMetric]
        [Description("Number of gated JSON RPC requests shed after waiting JsonRpc.MaxQueueWaitMs without being granted an execution slot (lighter requests are served first), per cost class.")]
        [KeyIsLabel("cost_class")]
        public static ConcurrentDictionary<RpcMethodCostClass, long> RpcAdmissionWaitTimeoutRejections { get; } = new();

        [GaugeMetric]
        [Description("Number of gated JSON RPC requests currently waiting for an execution slot, per cost class.")]
        [KeyIsLabel("cost_class")]
        public static ConcurrentDictionary<RpcMethodCostClass, long> RpcAdmissionQueued { get; } = new();

        [GaugeMetric]
        [Description("Number of gated JSON RPC requests currently executing, per cost class.")]
        [KeyIsLabel("cost_class")]
        public static ConcurrentDictionary<RpcMethodCostClass, long> RpcAdmissionInFlight { get; } = new();

        [GaugeMetric]
        [Description("Exponentially weighted moving average of the per-unit-weight service time of gated JSON RPC requests, in milliseconds, per cost class.")]
        [KeyIsLabel("cost_class")]
        public static ConcurrentDictionary<RpcMethodCostClass, double> RpcAdmissionServiceTimeMs { get; } = new();

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

    internal sealed class JsonRpcMetricLabels(string method, bool success) : IMetricLabels
    {
        private readonly string[] _labels = [method, success ? "success" : "fail"];

        public string[] Labels => _labels;
    }
}
