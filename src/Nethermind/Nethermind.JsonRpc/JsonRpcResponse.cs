/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Json;
using Nethermind.Dirichlet.Numerics;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc
{
    public class JsonRpcResponse
    {
        [JsonProperty(PropertyName = "jsonrpc", Order = 1)]
        public string JsonRpc { get; set; }

        [JsonProperty(PropertyName = "result", NullValueHandling = NullValueHandling.Include, Order = 2)]
        public object Result { get; set; }

        [JsonConverter(typeof(UInt256Converter), NumberConversion.Decimal)]
        [JsonProperty(PropertyName = "id", Order = 0)]
        public UInt256 Id { get; set; }
    }
    
    public class JsonRpcErrorResponse : JsonRpcResponse
    {
        [JsonProperty(PropertyName = "result", NullValueHandling = NullValueHandling.Ignore, Order = 2)]
        public new object Result { get; set; }
        
        [JsonProperty(PropertyName = "error", NullValueHandling = NullValueHandling.Ignore, Order = 3)]
        public Error Error { get; set; }
    }

    public class JsonRpcResponse<T>
    {
        [JsonProperty(PropertyName = "jsonrpc", Order = 1)]
        public string JsonRpc { get; set; }

        [JsonProperty(PropertyName = "result", NullValueHandling = NullValueHandling.Ignore, Order = 2)]
        public T Result { get; set; }

        [JsonProperty(PropertyName = "error", NullValueHandling = NullValueHandling.Ignore, Order = 3)]
        public Error Error { get; set; }

        [JsonConverter(typeof(UInt256Converter), NumberConversion.Decimal)]
        [JsonProperty(PropertyName = "id", Order = 0)]
        public UInt256 Id { get; set; }
    }
}