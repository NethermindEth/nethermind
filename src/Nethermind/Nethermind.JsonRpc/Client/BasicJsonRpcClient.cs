// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Data;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Client
{
    public class BasicJsonRpcClient : IJsonRpcClient, IDisposable
    {
        private readonly HttpClient _client;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;

        public BasicJsonRpcClient(Uri uri, IJsonSerializer jsonSerializer, ILogManager logManager) :
            this(uri, jsonSerializer, logManager, /*support long block traces better, default 100s might be too small*/ TimeSpan.FromMinutes(5))
        { }
        public BasicJsonRpcClient(Uri uri, IJsonSerializer jsonSerializer, ILogManager logManager, TimeSpan timeout)
        {
            _logger = logManager?.GetClassLogger<BasicJsonRpcClient>() ?? throw new ArgumentNullException(nameof(logManager));
            _jsonSerializer = jsonSerializer;

            _client = new HttpClient { BaseAddress = uri };
            _client.Timeout = timeout;
            _client.DefaultRequestHeaders.Accept.Clear();
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            AddAuthorizationHeader();
        }

        public async Task<string?> Post(string method, params object?[] parameters)
        {
            string request = GetJsonRequest(method, parameters);
            using StringContent requestContent = new(request, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _client.PostAsync("", requestContent);
            return await response.Content.ReadAsStringAsync();
        }

        public async Task<T?> Post<T>(string method, params object?[] parameters)
        {
            string responseString = string.Empty;
            try
            {
                string request = GetJsonRequest(method, parameters);
                using StringContent requestContent = new(request, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _client.PostAsync("", requestContent);
                responseString = await response.Content.ReadAsStringAsync();
                if (_logger.IsTrace) _logger.Trace(responseString);

                JsonRpcResponse<T> jsonResponse = _jsonSerializer.Deserialize<JsonRpcResponse<T>>(responseString);
                if (jsonResponse.Error is not null)
                {
                    if (_logger.IsError) _logger.Error(string.Concat(jsonResponse.Error.Message, " | ", jsonResponse.Error.Data));
                }

                return jsonResponse.Result;
            }
            catch (Exception e) when
            (
                e is not TaskCanceledException &&
                e is not HttpRequestException &&
                e is not NotImplementedException &&
                e is not NotSupportedException
            )
            {
                throw new DataException($"Cannot deserialize {responseString}", e);
            }
        }

        private string GetJsonRequest(string method, IEnumerable<object?> parameters)
        {
            var request = new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters ?? [],
                id = 67
            };

            return _jsonSerializer.Serialize(request);
        }

        private void AddAuthorizationHeader()
        {
            string url = _client.BaseAddress.ToString();
            if (!url.Contains('@'))
            {
                return;
            }

            string[] urlData = url.Split("://");
            string data = urlData[1].Split("@")[0];
            string encodedData = Base64Encode(data);
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encodedData);
        }

        private static string Base64Encode(string plainText)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public virtual void Dispose()
        {
            _client.Dispose();
        }
    }
}
