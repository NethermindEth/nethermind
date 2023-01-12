// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Nethermind.JsonRpc
{
    public readonly struct JsonRpcResult
    {
        [MemberNotNullWhen(true, nameof(BatchedResponses))]
        [MemberNotNullWhen(false, nameof(Response))]
        public bool IsCollection { get; }
        public IJsonRpcBatchResult? BatchedResponses { get; }
        public Entry? Response { get; }

        private JsonRpcResult(IJsonRpcBatchResult batchedResponses)
        {
            IsCollection = true;
            BatchedResponses = batchedResponses;
        }

        private JsonRpcResult(Entry singleResult)
        {
            IsCollection = false;
            Response = singleResult;
        }

        public static JsonRpcResult Single(JsonRpcResponse response, RpcReport report)
            => new(new Entry(response, report));

        public static JsonRpcResult Single(Entry entry)
            => new(entry);

        public static JsonRpcResult Collection(IJsonRpcBatchResult responses)
            => new(responses);

        public readonly struct Entry : IDisposable
        {
            public JsonRpcResponse Response { get; }
            public RpcReport Report { get; }

            public Entry(JsonRpcResponse response, RpcReport report)
            {
                Response = response;
                Report = report;
            }

            public void Dispose()
            {
                Response?.Dispose();
            }
        }
    }
}
