// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Primitives;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class JsonRpcContext
    {
        /// <summary>The context of the request whose RPC method is executing synchronously on this thread, if any.</summary>
        /// <remarks>
        /// Thread-static rather than async-local because publishing an async-local value allocates on every request.
        /// Only valid before the method's first await; after that it may be <see langword="null"/> or belong to another
        /// request, so it is no authentication signal there. Use <see cref="IContextAwareRpcModule.Context"/> instead.
        /// </remarks>
        [field: ThreadStatic]
        public static JsonRpcContext? Current { get; internal set; }

        public static JsonRpcContext Http(JsonRpcUrl url) => new(RpcEndpoint.Http, url: url);
        public static JsonRpcContext WebSocket(JsonRpcUrl url) => new(RpcEndpoint.Ws, url: url);

        public JsonRpcContext(RpcEndpoint rpcEndpoint, IJsonRpcDuplexClient? duplexClient = null, JsonRpcUrl? url = null)
        {
            RpcEndpoint = rpcEndpoint;
            DuplexClient = duplexClient;
            Url = url;
            IsAuthenticated = Url?.IsAuthenticated == true || RpcEndpoint == RpcEndpoint.IPC;
        }

        public RpcEndpoint RpcEndpoint { get; }
        public IJsonRpcDuplexClient? DuplexClient { get; }
        public JsonRpcUrl? Url { get; }
        public bool IsAuthenticated { get; }

        /// <summary>The transport cancellation signal for modules that cooperate with request cancellation.</summary>
        public CancellationToken RequestAborted { get; init; }

        /// <summary>Optional per-batch execution and envelope budgets imposed by the transport.</summary>
        public JsonRpcBatchLimits? BatchLimits { get; init; }

        /// <summary>Uses strict JSON-RPC 2.0 notification classification and invalid-envelope errors.</summary>
        public bool StrictEnvelope { get; init; }

        /// <summary>Address of the remote caller, or <see langword="null"/> when the transport has none (IPC).</summary>
        public IPAddress? RemoteAddress { get; init; }

        /// <summary>Identifies the client for per-client limits: the connection for WebSocket and IPC, the remote address for HTTP.</summary>
        /// <remarks><see langword="null"/> when the transport gives no way to tell clients apart.</remarks>
        public object? ClientKey => (object?)DuplexClient ?? (RemoteAddress?.IsIPv4MappedToIPv6 == true ? RemoteAddress.MapToIPv4() : RemoteAddress);

        /// <summary>Raw <c>X-Forwarded-For</c> header values of the request, empty when absent.</summary>
        /// <remarks>
        /// Set by the client or any proxy on the way, so it is only fit for logging, never for trust decisions.
        /// Kept as <see cref="StringValues"/> so capturing it never builds a string; it is joined only when a debug line is formatted.
        /// </remarks>
        public StringValues ForwardedFor { get; init; }
    }
}
