// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

        public IDictionary<string, string>? ResponseHeaders { get; private set; }

        public static void SetResponseHeader(string key, string value)
        {
            JsonRpcContext? context = Current.Value;
            if (context is null)
            {
                return;
            }

            context.ResponseHeaders ??= new Dictionary<string, string>();
            context.ResponseHeaders[key] = value;
        }

        public void Dispose()
        {
            if (Current.Value == this)
            {
                Current.Value = null;
            }
        }
    }
}
