// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Threading;
using Nethermind.Core.Attributes;

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
        [Description("Number of JSON RPC requests processed with errors.")]
        public static long JsonRpcErrors { get; set; }

        [CounterMetric]
        [Description("Number of JSON RPC requests processed succesfully.")]
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
    }
}
