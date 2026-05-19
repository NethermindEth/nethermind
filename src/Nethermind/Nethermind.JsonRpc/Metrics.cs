// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
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
        [Description("Number of JSON RPC HTTP envelopes parsed on the direct UTF-8 path.")]
        public static long JsonRpcDirectUtf8Parses;

        [CounterMetric]
        [Description("Number of JSON RPC HTTP documents parsed through the JsonDocument fallback path.")]
        public static long JsonRpcJsonDocumentFallbackParses;

        [CounterMetric]
        [Description("Number of JSON RPC requests that were invalid.")]
        public static long JsonRpcInvalidRequests { get; set; }

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
        [Description("Number of JSON RPC HTTP requests received with a Content-Length header.")]
        public static long JsonRpcHttpRequestsWithContentLength;

        [CounterMetric]
        [Description("Number of JSON RPC HTTP requests received without a Content-Length header.")]
        public static long JsonRpcHttpRequestsWithoutContentLength;

        [CounterMetric]
        [Description("Number of JSON RPC HTTP request body reads.")]
        public static long JsonRpcHttpRequestBodyReads;

        [CounterMetric]
        [Description("Number of JSON RPC HTTP request body segments read.")]
        public static long JsonRpcHttpRequestBodySegments;

        [CounterMetric]
        [Description("Number of JSON RPC bytes received through web sockets.")]
        public static long JsonRpcBytesReceivedWebSockets;

        [CounterMetric]
        [Description("Number of JSON RPC bytes received through IPC.")]
        public static long JsonRpcBytesReceivedIpc;

        [SummaryMetric(LabelNames = ["method", "status"], ObjectiveQuantile = [0.5, 0.9, 0.95, 0.99], ObjectiveEpsilon = [0.05, 0.05, 0.01, 0.005])]
        [Description("Individual rpc latency metric calls")]
        public static IMetricObserver JsonRpcCallLatencyMicros = NoopMetricObserver.Instance;

        [SummaryMetric(LabelNames = ["method", "status"], ObjectiveQuantile = [0.5, 0.9, 0.95, 0.99], ObjectiveEpsilon = [0.05, 0.05, 0.01, 0.005])]
        [Description("JSON RPC boundary latency outside the called RPC method body.")]
        public static IMetricObserver JsonRpcBoundaryLatencyMicros = NoopMetricObserver.Instance;

        [SummaryMetric(LabelNames = ["method", "status"], ObjectiveQuantile = [0.5, 0.9, 0.95, 0.99], ObjectiveEpsilon = [0.05, 0.05, 0.01, 0.005])]
        [Description("JSON RPC latency before invoking the called RPC method.")]
        public static IMetricObserver JsonRpcPreMethodBoundaryLatencyMicros = NoopMetricObserver.Instance;

        [SummaryMetric(LabelNames = ["method", "status"], ObjectiveQuantile = [0.5, 0.9, 0.95, 0.99], ObjectiveEpsilon = [0.05, 0.05, 0.01, 0.005])]
        [Description("JSON RPC latency spent inside the called RPC method body.")]
        public static IMetricObserver JsonRpcMethodBodyLatencyMicros = NoopMetricObserver.Instance;

        [SummaryMetric(LabelNames = ["method", "status"], ObjectiveQuantile = [0.5, 0.9, 0.95, 0.99], ObjectiveEpsilon = [0.05, 0.05, 0.01, 0.005])]
        [Description("JSON RPC latency after the called RPC method body returns and before response writing.")]
        public static IMetricObserver JsonRpcPostMethodBoundaryLatencyMicros = NoopMetricObserver.Instance;

        [SummaryMetric(LabelNames = ["method", "status"], ObjectiveQuantile = [0.5, 0.9, 0.95, 0.99], ObjectiveEpsilon = [0.05, 0.05, 0.01, 0.005])]
        [Description("JSON RPC response writing latency.")]
        public static IMetricObserver JsonRpcResponseWriteLatencyMicros = NoopMetricObserver.Instance;

        [SummaryMetric(LabelNames = ["method", "status"], ObjectiveQuantile = [0.5, 0.9, 0.95, 0.99], ObjectiveEpsilon = [0.05, 0.05, 0.01, 0.005])]
        [Description("JSON RPC response PipeWriter flush latency during response writing.")]
        public static IMetricObserver JsonRpcResponseFlushLatencyMicros = NoopMetricObserver.Instance;

        [SummaryMetric(LabelNames = ["method", "status"], ObjectiveQuantile = [0.5, 0.9, 0.95, 0.99], ObjectiveEpsilon = [0.05, 0.05, 0.01, 0.005])]
        [Description("JSON RPC response PipeWriter flush count during response writing.")]
        public static IMetricObserver JsonRpcResponseFlushCount = NoopMetricObserver.Instance;
    }
}
