// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.JsonRpc
{
    public readonly struct JsonRpcResult : IDisposable
    {
        public bool IsCollection { get; }

        public IAsyncEnumerable<JsonRpcResult>? BatchedResponses { get; }

        public JsonRpcResponse Response { get; }
        public RpcReport Report { get; }

        private JsonRpcResult(IAsyncEnumerable<JsonRpcResult> batchedResponses)
        {
            IsCollection = true;
            BatchedResponses = batchedResponses;
        }

        private JsonRpcResult(JsonRpcResponse response, RpcReport report)
        {
            IsCollection = false;
            Response = response;
            Report = report;
        }

        public static JsonRpcResult Single(JsonRpcResponse response, RpcReport report)
            => new(response, report);

        public static JsonRpcResult Collection(IAsyncEnumerable<JsonRpcResult> responses)
            => new(responses);

        public void Dispose()
        {
            Response?.Dispose();
            if (BatchedResponses is not null)
            {
                // Noop.
            }
        }
    }
}
