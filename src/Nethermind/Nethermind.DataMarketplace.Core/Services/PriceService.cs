using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Newtonsoft.Json;

[assembly: InternalsVisibleTo("Nethermind.DataMarketplace.Test")]
namespace Nethermind.DataMarketplace.Core.Services
{
    public class PriceService : IPriceService
    {
        private const string Url = "https://poloniex.com/public?command=returnTicker";
        private readonly IHttpClient _httpClient;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, PriceInfo>
            _prices = new ConcurrentDictionary<string, PriceInfo>();
        private ulong _updatedAt;
        private readonly ulong _updateInterval;
        
        public PriceService(IHttpClient httpClient, ITimestamper timestamper, ILogManager logManager,
            ulong updateInterval = 1)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _updatedAt = _timestamper.UnixTime.Seconds;
            _updateInterval = updateInterval;
        }
        
        public async Task UpdateAsync(params string[] currencies)
        {
            var currentTime = _timestamper.UnixTime.Seconds;
            if (_updatedAt + _updateInterval > currentTime || Interlocked.Exchange(ref _updatedAt, currentTime) == currentTime)
            {
                return;
            }

            var results = await _httpClient.GetAsync<Dictionary<string, PriceResult>>(Url);
            if (results is null)
            {
                if (_logger.IsWarn) _logger.Warn("There was an error when updating price.");
                return;
            }

            foreach (var currency in currencies)
            {
                if (!results.TryGetValue(currency, out var result))
                {
                    if (_logger.IsWarn) _logger.Warn($"There was an error when updating {currency} price.");
                    continue;
                }

                if (result.PriceUsd <= 0)
                {
                    if (_logger.IsWarn) _logger.Warn($"There was an error when updating {currency} price.");
                    continue;
                }
                
                _prices.AddOrUpdate(currency, _ => new PriceInfo(result.PriceUsd, currentTime),
                    (_, p) => new PriceInfo(result.PriceUsd, currentTime));
                if (_logger.IsInfo) _logger.Info($"Updated {currency} price: {result.PriceUsd} USD, updated at: {currentTime}");
            }
            //
            // if (!results.ContainsKey("USDT_DAI"))
            // {
            //     return;
            // }
            //
            // bool success = results.TryGetValue("USDT_DAI", out Result? result);
            // if (!success || result is null || result.PriceUsd <= 0)
            // {
            //     if (_logger.IsWarn) _logger.Warn($"There was an error when updating DAI price. Latest know value is: {UsdPrice} USD");
            //     return;
            // }
            //
            // UpdatedAt = currentTime;
            // UsdPrice = result.PriceUsd;
            //
            // if (_logger.IsInfo) _logger.Info($"Updated DAI price: {UsdPrice} USD, updated at: {UpdatedAt}");
        }

        public PriceInfo? Get(string currency) => _prices.TryGetValue(currency, out var price) ? price : null;
    }
    
    internal class PriceResult
    {
        [JsonProperty("last")]
        public decimal PriceUsd { get; set; }
    }
}
