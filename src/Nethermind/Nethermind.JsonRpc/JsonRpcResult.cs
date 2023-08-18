// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.JsonRpc
{
    public readonly struct JsonRpcResult
    {
        [MemberNotNullWhen(true, nameof(BatchedResponses))]
        [MemberNotNullWhen(false, nameof(SingleResponse))]
        [MemberNotNullWhen(false, nameof(Response))]
        [MemberNotNullWhen(false, nameof(Report))]
        public bool IsCollection { get; }
        public IJsonRpcBatchResult? BatchedResponses { get; }
        public Entry? SingleResponse { get; }
        public JsonRpcResponse? Response => SingleResponse?.Response;
        public RpcReport? Report => SingleResponse?.Report;

        private JsonRpcResult(IJsonRpcBatchResult batchedResponses)
        {
            IsCollection = true;
            BatchedResponses = batchedResponses;
        }

        private JsonRpcResult(Entry singleResult)
        {
            IsCollection = false;
            SingleResponse = singleResult;
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
