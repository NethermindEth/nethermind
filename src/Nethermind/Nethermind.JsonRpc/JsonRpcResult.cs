// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc
{
    public readonly struct JsonRpcResult : IDisposable
    {
        public Entry? SingleResponse { get; }
        public JsonRpcResponse? Response => SingleResponse?.Response;
        public RpcReport? Report => SingleResponse?.Report;

        private JsonRpcResult(in Entry singleResult) => SingleResponse = singleResult;

        public static JsonRpcResult Single(JsonRpcResponse response, in RpcReport report) => new(new Entry(response, report));

        public static JsonRpcResult Single(in Entry entry) => new(entry);

        public readonly struct Entry(JsonRpcResponse response, RpcReport report) : IDisposable
        {
            public JsonRpcResponse Response { get; } = response;
            public RpcReport Report { get; } = report;

            public void Dispose() => Response?.Dispose();
        }

        public void Dispose() => SingleResponse?.Dispose();
    }
}
