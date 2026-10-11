// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
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
            Current.Value = this;
        }

        public RpcEndpoint RpcEndpoint { get; }
        public IJsonRpcDuplexClient? DuplexClient { get; }
        public JsonRpcUrl? Url { get; }
        public bool IsAuthenticated { get; }

        /// <summary>Address of the remote caller, or <see langword="null"/> when the transport has none (IPC).</summary>
        public IPAddress? RemoteAddress { get; init; }

        /// <summary>Raw <c>X-Forwarded-For</c> header of the request, or <see langword="null"/> when absent.</summary>
        /// <remarks>Set by the client or any proxy on the way, so it is only fit for logging, never for trust decisions.</remarks>
        public string? ForwardedFor { get; init; }

        public void Dispose()
        {
            if (Current.Value == this)
            {
                Current.Value = null;
            }
        }
    }
}
