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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Logging;

namespace Nethermind.Runner.TestClient
{
    public class RunnerTestClient : IRunnerTestCient
    {
        private readonly HttpClient _client;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;

        public RunnerTestClient(ILogger logger, IJsonSerializer jsonSerializer)
        {
            _logger = logger;
            _jsonSerializer = jsonSerializer;

            _client = new HttpClient {BaseAddress = new Uri("http://127.0.0.1:8545")};
            _client.DefaultRequestHeaders.Accept.Clear();
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<string> Post(string method, params object[] parameters)
        {
            try
            {
                string request = GetJsonRequest(method, parameters);
                HttpResponseMessage response = await _client.PostAsync("", new StringContent(request, Encoding.UTF8, "application/json"));
                string content = await response.Content.ReadAsStringAsync();
                return content;
            }
            catch (Exception e)
            {
                _logger.Error($"Error during execution of {method}", e);
                return $"Error: {e.Message}";
            }
        }

        public string GetJsonRequest(string method, IEnumerable<object> parameters)
        {
            var request = new
            {
                jsonrpc = "2.0",
                method,
                Params = parameters ?? Enumerable.Empty<object>(),
                id = 67
            };
            return _jsonSerializer.Serialize(request);
        }
    }
}