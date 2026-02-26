// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class JsonRpcContext : IDisposable
    {
        [ThreadStatic]
        private static JsonRpcContext? _current;

        public static ThreadStaticAccessor Current { get; } = new();

        public static JsonRpcContext Http(JsonRpcUrl url) => new(RpcEndpoint.Http, url: url);
        public static JsonRpcContext WebSocket(JsonRpcUrl url) => new(RpcEndpoint.Ws, url: url);

        public JsonRpcContext(RpcEndpoint rpcEndpoint, IJsonRpcDuplexClient? duplexClient = null, JsonRpcUrl? url = null)
        {
            RpcEndpoint = rpcEndpoint;
            DuplexClient = duplexClient;
            Url = url;
            IsAuthenticated = Url?.IsAuthenticated == true || RpcEndpoint == RpcEndpoint.IPC;
            _current = this;
        }

        public RpcEndpoint RpcEndpoint { get; }
        public IJsonRpcDuplexClient? DuplexClient { get; }
        public JsonRpcUrl? Url { get; }
        public bool IsAuthenticated { get; }

        /// <summary>
        /// Headers to be added to the HTTP response. Handlers can add headers here,
        /// and they will be applied before the response body is written.
        /// </summary>
        public IDictionary<string, string>? ResponseHeaders { get; private set; }

        /// <summary>
        /// Adds a header to the current context's response headers.
        /// Thread-safe and creates the dictionary if needed.
        /// </summary>
        public static void SetResponseHeader(string key, string value)
        {
            JsonRpcContext? context = _current;
            if (context is null)
                return;

            context.ResponseHeaders ??= new Dictionary<string, string>();
            context.ResponseHeaders[key] = value;
        }

        public void Dispose()
        {
            if (_current == this)
            {
                _current = null;
            }
        }

        /// <summary>
        /// Provides .Value accessor compatible with AsyncLocal API shape so callers
        /// like <c>JsonRpcContext.Current.Value?.IsAuthenticated</c> continue to compile.
        /// </summary>
        public sealed class ThreadStaticAccessor
        {
            public JsonRpcContext? Value
            {
                get => _current;
                set => _current = value;
            }
        }
    }
}
