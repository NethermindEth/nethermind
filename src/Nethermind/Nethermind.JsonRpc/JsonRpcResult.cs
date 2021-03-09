//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            if (Responses != null)
            {
                for (var i = 0; i < Responses.Count; i++)
                {
                    Responses[i]?.Dispose();
                }
            }
        }
    }
}
