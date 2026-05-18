// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
