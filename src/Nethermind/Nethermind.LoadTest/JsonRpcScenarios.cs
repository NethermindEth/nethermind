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

using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Nethermind.LoadTest
{
    internal class JsonRpcScenarios
    {
        private readonly string _url;

        public JsonRpcScenarios(string url = "http://localhost:8545")
        {
            _url = url;
        }

        public Scenario eth_blockNumber
            => Build(nameof(eth_blockNumber));

        public Scenario eth_getBalance
            => Build(nameof(eth_getBalance), new object[] {"0xe79f0405208f5d23001aef0a1de9cf1083a03a2a", "latest"});

        public Scenario eth_getBlockByNumber
            => Build(nameof(eth_getBlockByNumber), new object[] {"latest", false});

        private Scenario Build(string method, object[] @params = null)
        {
            var request = new JsonRpcRequest(method, @params);
            var json = System.Text.Json.JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var step = HttpStep.Create(method, ctx =>
                Task.FromResult(Http.CreateRequest("POST", _url)
                    .WithHeader("content-type", "application/json")
                    .WithBody(content)
                    .WithCheck(response => Task.FromResult(response.IsSuccessStatusCode))));

            return ScenarioBuilder.CreateScenario($"Nethermind -> {method}()", step)
                .WithConcurrentCopies(100)
                .WithDuration(TimeSpan.FromSeconds(10));
        }
    }
}