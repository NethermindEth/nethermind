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
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class GasPriceServiceTests
    {
        private const string CustomType = "custom";
        private const string ConfigId = "ndm";
        private NdmConfig _config;
        private IHttpClient _client;
        private IConfigManager _configManager;
        private ITimestamper _timestamper;
        private IGasPriceService _gasPriceService;

        [SetUp]
        public void Setup()
        {
            _client = Substitute.For<IHttpClient>();
            _configManager = Substitute.For<IConfigManager>();
            _config = new NdmConfig();
            _configManager.GetAsync(ConfigId).Returns(_config);
            _timestamper = new Timestamper(DateTime.UtcNow);
            _gasPriceService = new GasPriceService(_client, _configManager, ConfigId, _timestamper,
                LimboLogs.Instance);
        }

        [Test]
        public void default_gas_price_should_be_20_gwei()
        {
            _config.GasPrice.Should().Be(20.GWei());
        }

        [Test]
        public void default_gas_price_type_should_be_custom()
        {
            _config.GasPriceType.Should().Be(CustomType);
        }

        [Test]
        public async Task get_current_should_return_chosen_gas_price()
        {
            _config.GasPrice = 10.GWei();
            var gasPrice = await _gasPriceService.GetCurrentAsync();
            gasPrice.Should().Be(_config.GasPrice);
            await _configManager.Received().GetAsync(ConfigId);
        }

        [TestCase("10000000000", NumberStyles.Integer)]
        [TestCase("0x2540be400", NumberStyles.HexNumber)]
        public async Task set_should_parse_custom_gas_price_value_and_update_config(string value, NumberStyles style)
        {
            await _gasPriceService.SetAsync(value);
            _config.GasPrice = UInt256.Parse(value.StartsWith("0x") ? value.Substring(2) : value, style);
            _config.GasPriceType = CustomType;
            await _configManager.Received().GetAsync(ConfigId);
            await _configManager.Received().UpdateAsync(_config);
        }

        [Test]
        public void set_should_fail_if_type_is_empty()
        {
            const string type = "";
            Func<Task> act = () => _gasPriceService.SetAsync(type);
            act.Should().Throw<ArgumentException>()
                .WithMessage("Gas price type cannot be empty. (Parameter 'type')");
        }

        [Test]
        public void set_should_fail_if_type_is_invalid()
        {
            const string type = "test";
            Func<Task> act = () => _gasPriceService.SetAsync(type);
            act.Should().Throw<ArgumentException>()
                .WithMessage($"Invalid gas price type: {type}. (Parameter 'type')");
        }

        [Test]
        public void set_should_fail_if_type_returns_0_price()
        {
            const string type = "safelow";
            Func<Task> act = () => _gasPriceService.SetAsync(type);
            act.Should().Throw<ArgumentException>()
                .WithMessage($"Gas price type: {type} couldn't be updated (price is 0). (Parameter 'type')");
        }
        
        [Test]
        public async Task update_async_should_set_default_types_if_client_returns_no_result()
        {
            await _gasPriceService.UpdateAsync();
            _gasPriceService.Types.SafeLow.Should().Be(GasPriceDetails.Empty);
            _gasPriceService.Types.Average.Should().Be(GasPriceDetails.Empty);
            _gasPriceService.Types.Fast.Should().Be(GasPriceDetails.Empty);
            _gasPriceService.Types.Fastest.Should().Be(GasPriceDetails.Empty);
            _gasPriceService.Types.Custom.Should().Be(new GasPriceDetails(_config.GasPrice, 0));
            _gasPriceService.Types.Type.Should().Be("custom");
            _gasPriceService.Types.UpdatedAt.Should().Be(_timestamper.EpochSeconds);
            await _configManager.Received().GetAsync(ConfigId);
            await _client.Received().GetAsync<GasPriceService.Result>(Arg.Any<string>());
        }

        [Test]
        public async Task update_async_should_set_types_if_client_returns_result()
        {
            var result = new GasPriceService.Result
            {
                SafeLow = 1,
                SafeLowWait = 1000,
                Average = 10,
                AvgWait = 100,
                Fast = 100,
                FastWait = 10,
                Fastest = 1000,
                FastestWait = 1
            };
            _client.GetAsync<GasPriceService.Result>(Arg.Any<string>()).Returns(result);
            await _gasPriceService.UpdateAsync();
            _gasPriceService.Types.SafeLow.Should()
                .Be(new GasPriceDetails(((int) result.SafeLow).GWei(), result.SafeLowWait));
            _gasPriceService.Types.Average.Should()
                .Be(new GasPriceDetails(((int) result.Average).GWei(), result.AvgWait));
            _gasPriceService.Types.Fast.Should()
                .Be(new GasPriceDetails(((int) result.Fast).GWei(), result.FastWait));
            _gasPriceService.Types.Fastest.Should()
                .Be(new GasPriceDetails(((int) result.Fastest).GWei(), result.FastestWait));
            _gasPriceService.Types.Custom.Should()
                .Be(new GasPriceDetails(_config.GasPrice, 0));
            _gasPriceService.Types.Type.Should().Be(_config.GasPriceType);
            _gasPriceService.Types.UpdatedAt.Should().Be(_timestamper.EpochSeconds);
            await _configManager.Received().GetAsync(ConfigId);
            await _client.Received().GetAsync<GasPriceService.Result>(Arg.Any<string>());
        }

        [TestCase("safelow")]
        [TestCase("average")]
        [TestCase("fast")]
        [TestCase("fastest")]
        public async Task set_should_parse_gas_price_type_and_update_config(string type)
        {
            var result = new GasPriceService.Result
            {
                SafeLow = 1,
                SafeLowWait = 1000,
                Average = 10,
                AvgWait = 100,
                Fast = 100,
                FastWait = 10,
                Fastest = 1000,
                FastestWait = 1
            };
            _client.GetAsync<GasPriceService.Result>(Arg.Any<string>()).Returns(result);
            await _gasPriceService.UpdateAsync();
            await _gasPriceService.SetAsync(type);
            _config.GasPriceType = type;
            switch (type)
            {
                case "safelow":
                    _config.GasPrice = _gasPriceService.Types.SafeLow.Price;
                    break;
                case "average":
                    _config.GasPrice = _gasPriceService.Types.Average.Price;
                    break;
                case "fast":
                    _config.GasPrice = _gasPriceService.Types.Fast.Price;
                    break;
                case "fastest":
                    _config.GasPrice = _gasPriceService.Types.Fastest.Price;
                    break;
            }

            await _configManager.Received().GetAsync(ConfigId);
            await _configManager.Received().UpdateAsync(_config);
        }
    }
}