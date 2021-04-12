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
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private abstract class JsonRpcDataSource : IDisposable
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
            
            protected async Task<TResult> SendRequest<TResult>(JsonRpcRequest request)
            {
                using HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, _uri)
                {
                    Content = new StringContent(_serializer.Serialize(request))
                };
                using HttpResponseMessage result = await _httpClient.SendAsync(message);
                return _serializer.Deserialize<TResult>(await result.Content.ReadAsStringAsync());
            }

            protected JsonRpcRequest CreateRequest(string methodName, params string[] parameters) =>
                new JsonRpcRequest()
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
        }
    }
}
