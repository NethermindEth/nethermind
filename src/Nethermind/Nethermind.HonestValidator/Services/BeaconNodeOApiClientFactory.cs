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

using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.OApiClient;
using Nethermind.HonestValidator.Configuration;

namespace Nethermind.HonestValidator.Services
{
    public class BeaconNodeOApiClientFactory
    {
        private readonly ILogger<BeaconNodeOApiClientFactory> _logger;
        private readonly IOptionsMonitor<BeaconNodeConnection> _beaconNodeConnectionOptions;
        private readonly IHttpClientFactory _httpClientFactory;

        public BeaconNodeOApiClientFactory(ILogger<BeaconNodeOApiClientFactory> logger, IOptionsMonitor<BeaconNodeConnection> beaconNodeConnectionOptions, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _beaconNodeConnectionOptions = beaconNodeConnectionOptions;
            _httpClientFactory = httpClientFactory;
        }
        
        public BeaconNodeOApiClient CreateClient()
        {
            BeaconNodeConnection beaconNodeConnection = _beaconNodeConnectionOptions.CurrentValue;
            string baseUrl = beaconNodeConnection.RemoteUrls[0];
            
            HttpClient httpClient = _httpClientFactory.CreateClient();
            
            BeaconNodeOApiClient beaconNodeOApiClient = new BeaconNodeOApiClient(baseUrl, httpClient);

            return beaconNodeOApiClient;
        }
    }
}