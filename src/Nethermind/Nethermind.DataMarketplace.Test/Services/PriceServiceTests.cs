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
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class PriceServiceTests
    {
        private IHttpClient _client;
        private IPriceService _priceService;
        private TestTimestamper _timestamper;
        private readonly DateTime _now = DateTime.UtcNow;

        [SetUp]
        public void Setup()
        {
            _client = Substitute.For<IHttpClient>();
            _timestamper = new TestTimestamper(_now);
            _priceService = new PriceService(_client, _timestamper, LimboLogs.Instance);
        }

        [Test]
        public async Task update_async_should_set_usd_price()
        {
            const decimal price = 187;
            const string currency = "USDT_ETH";
            var updatedAt = _timestamper.UtcNow.AddSeconds(10);
            _timestamper.UtcNow = updatedAt;
            var results = new Dictionary<string, PriceResult>
            {
                {
                    currency,
                    new PriceResult
                    {
                        PriceUsd = price
                    }
                }
            };
            
            _client.GetAsync<Dictionary<string, PriceResult>>(Arg.Any<string>()).ReturnsForAnyArgs(results);
            await _priceService.UpdateAsync(currency);
            var priceInfo = _priceService.Get(currency);
            priceInfo.Should().NotBeNull();
            priceInfo.UsdPrice.Should().Be(price);
            priceInfo.UpdatedAt.Should().Be(((ITimestamper)_timestamper).UnixTime.Seconds);
            await _client.ReceivedWithAnyArgs().GetAsync<Dictionary<string, PriceResult>>(Arg.Any<string>());
        }

        public class TestTimestamper : ITimestamper
        {
            public DateTime UtcNow { get; set; }

            public TestTimestamper(DateTime utcNow)
            {
                UtcNow = utcNow;
            }
        }
    }
}
