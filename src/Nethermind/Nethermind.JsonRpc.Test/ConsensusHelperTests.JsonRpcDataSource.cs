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
// 

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private abstract class JsonRpcDataSource<T> : IConsensusDataSource<T>
        {
            private readonly Uri _uri;
            protected readonly IJsonSerializer _serializer;
            private readonly HttpClient _httpClient;

            protected JsonRpcDataSource(Uri uri, IJsonSerializer serializer)
            {
                _uri = uri;
                _serializer = serializer;
                _httpClient = new HttpClient();
            }
            
            protected async Task<string> SendRequest(JsonRpcRequest request)
            {
                using HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, _uri)
                {
                    Content = new StringContent(_serializer.Serialize(request), Encoding.UTF8, "application/json")
                    
                };
                using HttpResponseMessage result = await _httpClient.SendAsync(message);
                string content = await result.Content.ReadAsStringAsync();
                return content;
            }

            protected JsonRpcRequestWithParams CreateRequest(string methodName, params object[] parameters) =>
                new JsonRpcRequestWithParams()
                {
                    Id = 1,
                    JsonRpc = "2.0",
                    Method = methodName,
                    Params = parameters
                };

            public void Dispose()
            {
                _httpClient?.Dispose();
            }

            protected class JsonRpcSuccessResponse<T> : JsonRpcSuccessResponse
            {
                [JsonProperty(PropertyName = "result", NullValueHandling = NullValueHandling.Include, Order = 1)]
                public new T Result { get { return (T)base.Result; } set { base.Result = value; } }
            }

            public virtual async Task<(T, string)> GetData()
            {
                string jsonData = await GetJsonData();
                return (_serializer.Deserialize<JsonRpcSuccessResponse<T>>(jsonData).Result, jsonData);
            }

            public abstract Task<string> GetJsonData();
            
            public class JsonRpcRequestWithParams : JsonRpcRequest
            {
                [JsonProperty(Required = Required.Default)]
                public new object[]? Params { get; set; }
            }
        }
    }
}
