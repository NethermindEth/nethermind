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
        [Description("Number of JSON RPC requests rejected or timed out at a module, shared-request, execution-environment, or synchronous-transaction concurrency limit.")]
        public static long JsonRpcOverloadRejections => _jsonRpcOverloadRejections;
        private static long _jsonRpcOverloadRejections;
        internal static void IncrementJsonRpcOverloadRejections() => Interlocked.Increment(ref _jsonRpcOverloadRejections);

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
