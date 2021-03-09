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
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;
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
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;
        private ulong _updatedAt;
        private readonly ulong _updateInterval;

        public GasPriceTypes? Types { get; private set; }

        public GasPriceService(IHttpClient client, IConfigManager configManager, string configId,
            ITimestamper timestamper, ILogManager logManager, ulong updateInterval = 5)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _configId = configId ?? throw new ArgumentNullException(nameof(configId));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _updateInterval = updateInterval;
        }

        public async Task<UInt256> GetCurrentGasPriceAsync()
        {
            NdmConfig? config = await _configManager.GetAsync(_configId);
            return config?.GasPrice ?? 20.GWei();
        }
        
        public async Task<UInt256> GetCurrentRefundGasPriceAsync()
        {
            NdmConfig? config = await _configManager.GetAsync(_configId);
            return config?.RefundGasPrice ?? 20.GWei();
        }

        public async Task SetGasPriceOrTypeAsync(string gasPriceOrType)
        {
            string type;
            bool isHex = gasPriceOrType.StartsWith("0x");
            if (isHex && gasPriceOrType.Length == 2)
            {
                throw new ArgumentException($"Invalid gas price: {gasPriceOrType}");
            }

            NumberStyles numberStyle = isHex ? NumberStyles.HexNumber : NumberStyles.Integer;
            string value = isHex ? gasPriceOrType.Substring(2) : gasPriceOrType;
            if (UInt256.TryParse(value, numberStyle, CultureInfo.InvariantCulture, out UInt256 gasPriceValue))
            {
                type = CustomType;
            }
            else
            {
                type = gasPriceOrType;
                gasPriceValue = GetForType(type);
                if (gasPriceValue == 0)
                {
                    throw new ArgumentException($"Gas price type: {type} couldn't be updated (price is 0).", nameof(type));
                }
            }

            NdmConfig? config = await _configManager.GetAsync(_configId);
            if (config == null)
            {
                if (_logger.IsError) _logger.Error($"Failed to retrieve config {_configId} to update gas price.");
                throw new InvalidOperationException($"Failed to retrieve config {_configId} to update gas price.");
            }
            else
            {
                config.GasPrice = gasPriceValue;
                config.GasPriceType = type;
                await _configManager.UpdateAsync(config);
                if (_logger.IsInfo) _logger.Info($"Updated gas price: {config.GasPrice} wei.");
            }
        }
        
        public async Task SetRefundGasPriceAsync(UInt256 gasPrice)
        {
            if (gasPrice <= 0)
            {
                throw new ArgumentException("Refund gas price must be greater than 0.");
            }

            NdmConfig? config = await _configManager.GetAsync(_configId);
            if (config == null)
            {
                if (_logger.IsError) _logger.Error($"Failed to retrieve config {_configId} to update refund gas price.");
                throw new InvalidOperationException($"Failed to retrieve config {_configId} to update refund gas price.");
            }

            config.RefundGasPrice = gasPrice;
            await _configManager.UpdateAsync(config);
            if (_logger.IsInfo) _logger.Info($"Updated refund gas price: {config.RefundGasPrice} wei.");
        }
        
        public async Task<UInt256> GetCurrentPaymentClaimGasPriceAsync()
        {
            NdmConfig? config = await _configManager.GetAsync(_configId);
            return config?.PaymentClaimGasPrice ?? 20.GWei();
        }
        
        public async Task SetPaymentClaimGasPriceAsync(UInt256 gasPrice)
        {
            if (gasPrice <= 0)
            {
                throw new ArgumentException("Payment claim gas price must be greater than 0.");
            }

            NdmConfig? config = await _configManager.GetAsync(_configId);
            if (config == null)
            {
                if (_logger.IsError) _logger.Error($"Failed to retrieve config {_configId} to update payment claim  gas price.");
                throw new InvalidOperationException($"Failed to retrieve config {_configId} to update payment claim  gas price.");
            }

            config.PaymentClaimGasPrice = gasPrice;
            await _configManager.UpdateAsync(config);
            if (_logger.IsInfo) _logger.Info($"Updated payment claim  gas price: {config.PaymentClaimGasPrice} wei.");
        }

        public async Task UpdateGasPriceAsync()
        {
            var currentTime = _timestamper.UnixTime.Seconds;
            var updatedAt = _updatedAt;
            if (_updatedAt + _updateInterval <= currentTime && Interlocked.CompareExchange(ref _updatedAt, currentTime, updatedAt) == updatedAt)
            {
                NdmConfig? config = await _configManager.GetAsync(_configId);
                if (config == null)
                {
                    if (_logger.IsWarn) _logger.Warn("Cannot update gas price because of missing config.");
                    return;
                }

                Result result = await _client.GetAsync<Result>(Url);
                if (result is null)
                {
                    if (_logger.IsWarn) _logger.Warn($"There was an error when fetching the data from ETH Gas Station - using the latest gas price: {config.GasPrice} wei, type: {config.GasPriceType} as {CustomType}.");
                    Types = new GasPriceTypes(GasPriceDetails.Empty, GasPriceDetails.Empty, GasPriceDetails.Empty,
                        GasPriceDetails.Empty, new GasPriceDetails(config.GasPrice, 0), CustomType,
                        _updatedAt);
                    return;
                }

                string type = config.GasPriceType;
                GasPriceDetails custom = type.Equals("custom", StringComparison.InvariantCultureIgnoreCase)
                    ? new GasPriceDetails(config.GasPrice, 0)
                    : GasPriceDetails.Empty;

                Types = new GasPriceTypes(new GasPriceDetails(GetGasPriceGwei(result.SafeLow), result.SafeLowWait),
                    new GasPriceDetails(GetGasPriceGwei(result.Average), result.AvgWait),
                    new GasPriceDetails(GetGasPriceGwei(result.Fast), result.FastWait),
                    new GasPriceDetails(GetGasPriceGwei(result.Fastest), result.FastestWait),
                    custom, type, _updatedAt);

                if (_logger.IsInfo) _logger.Info($"Updated gas price, safeLow: {Types.SafeLow.Price} wei, average: {Types.Average.Price} wei, fast: {Types.Fast.Price} wei, fastest: {Types.Fastest.Price} wei, updated at: {_updatedAt}.");

                // ETH Gas Station returns 10xGwei value.
                UInt256 GetGasPriceGwei(decimal gasPrice) => ((int) Math.Ceiling(gasPrice / 10)).GWei();
            }
        }

        private UInt256 GetForType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new ArgumentException("Gas price type cannot be empty.", nameof(type));
            }

            type = type.ToLowerInvariant();
            if (!_types.Contains(type))
            {
                throw new ArgumentException($"Invalid gas price type: {type}.", nameof(type));
            }

            if (Types is null)
            {
                return 0;
            }

            return type.ToLowerInvariant() switch
            {
                "safelow" => Types.SafeLow.Price,
                "average" => Types.Average.Price,
                "fast" => Types.Fast.Price,
                "fastest" => Types.Fastest.Price,
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
