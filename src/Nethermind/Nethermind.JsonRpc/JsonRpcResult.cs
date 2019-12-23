//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;

namespace Nethermind.JsonRpc
{
    public struct JsonRpcResult
    {
        public bool IsCollection { get; }
        public IReadOnlyList<JsonRpcResponse> Responses { get; }
        public JsonRpcResponse Response { get; }

        private JsonRpcResult(IReadOnlyList<JsonRpcResponse> responses)
        {
            IsCollection = true;
            Responses = responses;
            Response = null;
        }
        
        private JsonRpcResult(JsonRpcResponse response)
        {
            IsCollection = false;
            Responses = null;
            Response = response;
        }

        public static JsonRpcResult Single(JsonRpcResponse response)
            => new JsonRpcResult(response);

        public static JsonRpcResult Collection(IReadOnlyList<JsonRpcResponse> responses)
            => new JsonRpcResult(responses);
    }
}