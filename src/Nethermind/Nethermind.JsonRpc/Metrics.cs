// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        [Description("Number of JSON RPC requests rejected or timed out at a concurrency cap (the EVM-execution admission gate — see the RpcAdmission* metrics, JsonRpc.EvmExecutionConcurrency and JsonRpc.MaxQueueWaitMs — a module pool, or the override-environment limit). A nonzero rate means callers receive 'Too many requests'.")]
        public static long JsonRpcOverloadRejections => _jsonRpcOverloadRejections;
        private static long _jsonRpcOverloadRejections;
        internal static void IncrementJsonRpcOverloadRejections() => Interlocked.Increment(ref _jsonRpcOverloadRejections);

        [GaugeMetric]
        [Description("Number of EVM-executing JSON RPC requests waiting for an execution slot. A request whose caller has disconnected stays counted until the next grant or expiry sweep removes it, at most JsonRpc.MaxQueueWaitMs.")]
        public static long RpcAdmissionQueued { get; set; }

        [GaugeMetric]
        [Description("Number of EVM-executing JSON RPC requests currently executing. A value pinned at JsonRpc.EvmExecutionConcurrency while RpcAdmissionQueued stays zero is the signature of a leaked permit.")]
        public static long RpcAdmissionInFlight { get; set; }

        [CounterMetric]
        [Description("Number of EVM-executing JSON RPC requests shed up front: predicted queue wait above JsonRpc.MaxQueueWaitMs, queueing disabled, or JsonRpc.RequestQueueLimit requests already waiting. Spikes on short bursts suggest a longer MaxQueueWaitMs.")]
        public static long RpcAdmissionPredictedWaitRejections { get; set; }

        [CounterMetric]
        [Description("Number of EVM-executing JSON RPC requests shed after waiting JsonRpc.MaxQueueWaitMs without being granted a slot (lighter requests are served first). A sustained rate means the node is saturated rather than bursty.")]
        public static long RpcAdmissionWaitTimeoutRejections { get; set; }

        [GaugeMetric]
        [Description("Exponentially weighted moving average of the service time per weight unit (128 KiB of params) of EVM-executing JSON RPC requests, in milliseconds.")]
        public static double RpcAdmissionServiceTimeMs { get; set; }

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
