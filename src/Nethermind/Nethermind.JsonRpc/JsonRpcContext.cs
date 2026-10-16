// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Primitives;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class JsonRpcContext : IDisposable
    {
        public static AsyncLocal<JsonRpcContext?> Current { get; } = new();

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

        /// <summary>Address of the remote caller, or <see langword="null"/> when the transport has none (IPC).</summary>
        public IPAddress? RemoteAddress { get; init; }

        /// <summary>Raw <c>X-Forwarded-For</c> header values of the request, empty when absent.</summary>
        /// <remarks>
        /// Set by the client or any proxy on the way, so it is only fit for logging, never for trust decisions.
        /// Kept as <see cref="StringValues"/> so capturing it never builds a string; it is joined only when a debug line is formatted.
        /// </remarks>
        public StringValues ForwardedFor { get; init; }

        /// <remarks>
        /// Clears <see cref="Current"/> only when the disposing scope made this context current. It is not the main cleanup:
        /// request processing publishes the context inside an async method, whose return restores the caller's value.
        /// </remarks>
        public void Dispose()
        {
            if (Current.Value == this)
            {
                Current.Value = null;
            }
        }
    }
}
