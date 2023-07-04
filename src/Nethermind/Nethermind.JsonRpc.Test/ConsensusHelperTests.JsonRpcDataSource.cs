// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private abstract class JsonRpcDataSource<T2> : IConsensusDataSource<T2>
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
                using HttpRequestMessage message = new(HttpMethod.Post, _uri)
                {
                    Content = new StringContent(_serializer.Serialize(request), Encoding.UTF8, "application/json")

                };
                using HttpResponseMessage result = await _httpClient.SendAsync(message);
                string content = await result.Content.ReadAsStringAsync();
                return content;
            }

            protected JsonRpcRequestWithParams CreateRequest(string methodName, params object[] parameters) =>
                new()
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

            public virtual async Task<(T2, string)> GetData()
            {
                string jsonData = await GetJsonData();
                return (_serializer.Deserialize<JsonRpcSuccessResponse<T2>>(jsonData).Result, jsonData);
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
