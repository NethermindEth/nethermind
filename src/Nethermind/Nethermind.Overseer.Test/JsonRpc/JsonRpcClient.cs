// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using DotNetty.Common.Utilities;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;

namespace Nethermind.Overseer.Test.JsonRpc
{
    public class JsonRpcClient : IJsonRpcClient
    {
        private readonly string _host;
        private readonly string _methodPrefix = "ndm_";
        private readonly HttpClient _client;

        public JsonRpcClient(string host)
        {
            _host = host;
            _client = new HttpClient
            {
                BaseAddress = new Uri(host)
            };
        }

        public Task<JsonRpcResponse<T>> PostAsync<T>(string method)
            => PostAsync<T>(method, Array.Empty<object>());

        public async Task<JsonRpcResponse<T>> PostAsync<T>(string method, object[] @params)
        {
            string methodToCall = method.Contains("_") ? method : $"{_methodPrefix}{method}";
            Console.WriteLine($"Sending {methodToCall} to {_host}");
            var request = new JsonRpcRequest(methodToCall, @params);
            var payload = GetPayload(request);
            string errorMessage = null;
            HttpResponseMessage response = await _client.PostAsync("/", payload).ContinueWith((t) =>
            {
                if (t.IsFaulted)
                {
                    errorMessage = t.Exception.Unwrap().Message;
                    return null;
                }
                else if (t.IsCanceled)
                {
                    return null;
                }

                return t.Result;
            });

            if (!(response?.IsSuccessStatusCode ?? false))
            {
                var result = new JsonRpcResponse<T>();
                result.Error = new JsonRpcResponse<T>.ErrorResponse(ErrorCodes.InternalError, errorMessage);
                return result;
            }

            return await response.Content.ReadAsStringAsync()
                .ContinueWith(t => new EthereumJsonSerializer().Deserialize<JsonRpcResponse<T>>(t.Result));
        }

        private StringContent GetPayload(JsonRpcRequest request)
            => new StringContent(new EthereumJsonSerializer().Serialize(request), Encoding.UTF8, "application/json");

        private class JsonRpcRequest
        {
            public string JsonRpc { get; set; }
            public int Id { get; set; }
            public string Method { get; set; }
            public object[] Params { get; set; }

            public JsonRpcRequest(string method, object[] @params) : this("2.0", 1, method, @params)
            {
            }

            public JsonRpcRequest(string jsonRpc, int id, string method, object[] @params)
            {
                JsonRpc = jsonRpc;
                Id = id;
                Method = method;
                Params = @params;
            }
        }
    }
}
