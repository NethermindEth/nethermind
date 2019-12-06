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

<<<<<<< HEAD
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
=======
using System.Threading.Tasks;
using FluentAssertions;
>>>>>>> test squash
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class EthPriceServiceTests
    {
        private IHttpClient _client;
        private IEthPriceService _ethPriceService;
<<<<<<< HEAD
        private ITimestamper _timestamper;
=======
>>>>>>> test squash

        [SetUp]
        public void Setup()
        {
            _client = Substitute.For<IHttpClient>();
<<<<<<< HEAD
            _timestamper = new Timestamper(DateTime.UtcNow);
            _ethPriceService = new EthPriceService(_client, _timestamper, LimboLogs.Instance);
=======
            _ethPriceService = new EthPriceService(_client, LimboLogs.Instance);
>>>>>>> test squash
        }

        [Test]
        public async Task update_async_should_set_usd_price()
        {
            const decimal price = 187;
            var results = new[]
            {
                new EthPriceService.Result
                {
                    PriceUsd = price
                }
            };
            _client.GetAsync<EthPriceService.Result[]>(Arg.Any<string>()).ReturnsForAnyArgs(results);
            await _ethPriceService.UpdateAsync();
            _ethPriceService.UsdPrice.Should().Be(price);
<<<<<<< HEAD
            _ethPriceService.UpdatedAt.Should().Be(_timestamper.EpochSeconds);
=======
>>>>>>> test squash
            await _client.ReceivedWithAnyArgs().GetAsync<EthPriceService.Result[]>(Arg.Any<string>());
        }
    }
}