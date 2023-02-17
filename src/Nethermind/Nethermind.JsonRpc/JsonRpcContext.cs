// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class JsonRpcContext
    {
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
    }
}
