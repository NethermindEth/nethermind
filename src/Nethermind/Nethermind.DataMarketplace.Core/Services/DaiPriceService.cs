using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Newtonsoft.Json;

[assembly: InternalsVisibleTo("Nethermind.DataMarketplace.Test")]
namespace Nethermind.DataMarketplace.Core.Services
{
    public class DaiPriceService : IDaiPriceService
    {
        private const string Url = "https://poloniex.com/public?command=returnTicker";
        private readonly IHttpClient _httpClient;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;
        
        public decimal UsdPrice { get; private set; }
        public ulong UpdatedAt { get; private set; }

        public DaiPriceService(IHttpClient httpClient, ITimestamper timestamper, ILogManager logManager)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        public async Task UpdateAsync()
        {
            var currentTime = _timestamper.UnixTime.Seconds;
            if (currentTime < UpdatedAt + 1)
            {
                return;
            }

            var results = await _httpClient.GetAsync<Dictionary<string, Result>>(Url);
            if (results is null || !results.ContainsKey("USDT_DAI"))
            {
                if (_logger.IsWarn) _logger.Warn($"There was an error when updating DAI price. Latest know value is: {UsdPrice} USD");
                return;
            }

            bool success = results.TryGetValue("USDT_DAI", out Result? result);
            if (!success || result is null || result.PriceUsd <= 0)
            {
                if (_logger.IsWarn) _logger.Warn($"There was an error when updating DAI price. Latest know value is: {UsdPrice} USD");
                return;
            }

            UpdatedAt = currentTime;
            UsdPrice = result.PriceUsd;
            
            
            if (_logger.IsInfo) _logger.Info($"Updated DAI price: {UsdPrice} USD, updated at: {UpdatedAt}");
        }

        internal class Result
        {
            [JsonProperty("last")]
            public decimal PriceUsd { get; set; }
        }
    }
}


