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
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Newtonsoft.Json;

[assembly: InternalsVisibleTo("Nethermind.DataMarketplace.Test")]
namespace Nethermind.DataMarketplace.Core.Services
{
    public class EthPriceService : IEthPriceService
    {
        // private const string Url = "https://api.coinmarketcap.com/v1/ticker/ethereum/?convert=USD";
        private const string Url = "https://poloniex.com/public?command=returnTicker";
        private readonly IHttpClient _client;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;
        
        public decimal UsdPrice { get; private set; }
        public ulong UpdatedAt { get; private set; }

        public EthPriceService(IHttpClient client, ITimestamper timestamper, ILogManager logManager)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task UpdateAsync()
        {
            var currentTime = _timestamper.EpochSeconds;
            if (currentTime < UpdatedAt + 1)
            {
                return;
            }

            // var results = await _client.GetAsync<Result[]>(Url);
            var results = await _client.GetAsync<Dictionary<string, Result>>(Url);
            if (!results.ContainsKey("USDT_ETH"))
            {
                if (_logger.IsWarn) _logger.Warn($"There was an error when updating ETH price. Latest know value is: {UsdPrice} USD");
                return;
            }

            bool success = results.TryGetValue("USDT_ETH", out Result? result);
            if (!success || result is null || result.PriceUsd <= 0)
            {
                if (_logger.IsWarn) _logger.Warn($"There was an error when updating ETH price. Latest know value is: {UsdPrice} USD");
                return;
            }

            UpdatedAt = currentTime;
            UsdPrice = result.PriceUsd;
            
            if (_logger.IsInfo) _logger.Info($"Updated ETH price: {UsdPrice} USD, updated at: {UpdatedAt}");
        }

        // internal class Result
        // {
        //     [JsonProperty("price_usd")]
        //     public decimal PriceUsd { get; set; }
        // }
        
        internal class Result
        {
            [JsonProperty("last")]
            public decimal PriceUsd { get; set; }
        }
    }
}