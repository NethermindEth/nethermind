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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Newtonsoft.Json;

[assembly: InternalsVisibleTo("Nethermind.DataMarketplace.Test")]
namespace Nethermind.DataMarketplace.Core.Services
{
    public class GasPriceService : IGasPriceService
    {
        private const string CustomType = "custom";
        private const string Url = "https://ethgasstation.info/json/ethgasAPI.json";
        private readonly string[] _types = {CustomType, "safelow", "average", "fast", "fastest"};
        private readonly IHttpClient _client;
        private readonly IConfigManager _configManager;
        private readonly string _configId;
        private readonly ILogger _logger;

        public GasPriceService(IHttpClient client, IConfigManager configManager, string configId,
            ILogManager logManager)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _configId = configId ?? throw new ArgumentNullException(nameof(configId));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task<GasPriceTypes> GetAvailableAsync()
        {
            var config = await _configManager.GetAsync(_configId);
            var result = await _client.GetAsync<Result>(Url);
            if (result is null)
            {
                if (_logger.IsWarn) _logger.Warn($"There was an error when fetching the data from ETH Gas Station - using the latest gas price: {config.GasPrice} wei, type: {config.GasPriceType} as {CustomType}.");
                return new GasPriceTypes(GasPriceDetails.Empty, GasPriceDetails.Empty, GasPriceDetails.Empty,
                    GasPriceDetails.Empty, new GasPriceDetails(config.GasPrice, 0), CustomType);
            }

            var type = config.GasPriceType;
            var custom = type.Equals("custom", StringComparison.InvariantCultureIgnoreCase)
                ? new GasPriceDetails(config.GasPrice, 0)
                : GasPriceDetails.Empty;

            return new GasPriceTypes(new GasPriceDetails(((int) result.SafeLow).GWei(), result.SafeLowWait),
                new GasPriceDetails(((int) result.Average).GWei(), result.AvgWait),
                new GasPriceDetails(((int) result.Fast).GWei(), result.FastWait),
                new GasPriceDetails(((int) result.Fastest).GWei(), result.FastestWait), custom, type);
        }

        public async Task<UInt256> GetCurrentAsync()
        {
            var config = await _configManager.GetAsync(_configId);

            return config.GasPrice;
        }


        public async Task SetAsync(string gasPriceOrType)
        {
            string type;
            if (UInt256.TryParse(gasPriceOrType, out var gasPriceValue))
            {
                type = CustomType;
            }
            else
            {
                type = gasPriceOrType;
                gasPriceValue = await GetForTypeAsync(type);
                if (gasPriceValue == 0)
                {
                    throw new ArgumentException($"Gas price type: {type} couldn't be updated.", nameof(type));
                }
            }

            var config = await _configManager.GetAsync(_configId);
            config.GasPrice = gasPriceValue;
            config.GasPriceType = type;
            await _configManager.UpdateAsync(config);
            if (_logger.IsInfo) _logger.Info($"Updated gas price: {config.GasPrice} wei.");
        }

        private async Task<UInt256> GetForTypeAsync(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new ArgumentException("Gas price type cannot be empty.", nameof(type));
            }

            type = type.ToLowerInvariant();
            if (!_types.Contains(type))
            {
                throw new ArgumentException($"Invalid gas price type: {type}", nameof(type));
            }

            var types = await GetAvailableAsync();
            if (types is null)
            {
                return 0;
            }

            return type.ToLowerInvariant() switch
            {
                "safelow" => types.SafeLow?.Price ?? 0,
                "average" => types.Average?.Price ?? 0,
                "fast" => types.Fast?.Price ?? 0,
                "fastest" => types.Fastest?.Price ?? 0,
                _ => UInt256.Zero
            };
        }

        internal class Result
        {
            [JsonProperty("fast")]
            public decimal Fast { get; set; }
            [JsonProperty("fastest")]
            public decimal Fastest { get; set; }
            [JsonProperty("safeLow")]
            public decimal SafeLow { get; set; }
            [JsonProperty("average")]
            public decimal Average { get; set; }
            [JsonProperty("safeLowWait")]
            public double SafeLowWait { get; set; }
            [JsonProperty("avgWait")]
            public double AvgWait { get; set; }
            [JsonProperty("fastWait")]
            public double FastWait { get; set; }
            [JsonProperty("fastestWait")]
            public double FastestWait { get; set; }
        }
    }
}