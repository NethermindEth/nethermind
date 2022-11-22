// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.JsonRpc
{
    public readonly struct JsonRpcResult : IDisposable
    {
        public bool IsCollection { get; }
        public IReadOnlyList<JsonRpcResponse> Responses { get; }
        public IReadOnlyList<RpcReport> Reports { get; }
        public JsonRpcResponse Response { get; }
        public RpcReport Report { get; }

        private JsonRpcResult(IReadOnlyList<JsonRpcResponse> responses, IReadOnlyList<RpcReport> reports)
        {
            IsCollection = true;
            Responses = responses;
            Reports = reports;
            Response = null;
            Report = default;
        }

        private JsonRpcResult(JsonRpcResponse response, RpcReport report)
        {
            IsCollection = false;
            Responses = null;
            Reports = null;
            Response = response;
            Report = report;
        }

        public static JsonRpcResult Single(JsonRpcResponse response, RpcReport report)
            => new(response, report);

        public static JsonRpcResult Collection(IReadOnlyList<JsonRpcResponse> responses, IReadOnlyList<RpcReport> reports)
            => new(responses, reports);

        public void Dispose()
        {
            Response?.Dispose();
            if (Responses is not null)
            {
                for (var i = 0; i < Responses.Count; i++)
                {
                    Responses[i]?.Dispose();
                }
            }
        }
    }
}
