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
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc
{
    public class JsonRpcResponse : IDisposable
    {
        private Action? _disposableAction;

        public JsonRpcResponse(Action? disposableAction = null)
        {
            _disposableAction = disposableAction;
        }
        
        [JsonProperty(PropertyName = "jsonrpc", Order = 0)]
        public readonly string JsonRpc = "2.0";

        [JsonConverter(typeof(IdConverter))]
        [JsonProperty(PropertyName = "id", Order = 2, NullValueHandling = NullValueHandling.Include)]
        public object? Id { get; set; }

        [JsonIgnore]
        public string MethodName { get; set; }

        public void Dispose()
        {
            _disposableAction?.Invoke();
            _disposableAction = null;
        }
    }

    public class JsonRpcSuccessResponse : JsonRpcResponse
    {
        [JsonProperty(PropertyName = "result", NullValueHandling = NullValueHandling.Include, Order = 1)]
        public object? Result { get; set; }

        public JsonRpcSuccessResponse(Action? disposableAction = null) : base(disposableAction)
        {
        }
    }

    public class JsonRpcErrorResponse : JsonRpcResponse
    {
        [JsonProperty(PropertyName = "error", NullValueHandling = NullValueHandling.Include, Order = 1)]
        public Error? Error { get; set; }

        public JsonRpcErrorResponse(Action? disposableAction = null) : base(disposableAction)
        {
        }
    }
}
