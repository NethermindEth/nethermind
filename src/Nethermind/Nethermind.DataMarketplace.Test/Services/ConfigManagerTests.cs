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

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class ConfigManagerTests
    {
        private string _configId;
        private NdmConfig _defaultConfig;
        private IConfigRepository _configRepository;
        private IConfigManager _configManager;

        [SetUp]
        public void Setup()
        {
            _configId = "ndm";
            _defaultConfig = new NdmConfig();
            _configRepository = Substitute.For<IConfigRepository>();
            _configManager = new ConfigManager(_defaultConfig, _configRepository);
        }

        [Test]
        public async Task given_store_config_in_database_equal_false_default_config_should_be_returned()
        {
            _defaultConfig.StoreConfigInDatabase = false;
            var config = await _configManager.GetAsync(_configId);
            config.Should().Be(_defaultConfig);
        }
        
        [Test]
        public async Task given_store_config_in_database_equal_true_config_from_database_should_be_returned()
        {
            _defaultConfig.StoreConfigInDatabase = true;
            var configFromDatabase = new NdmConfig();
            _configRepository.GetAsync(_configId).Returns(configFromDatabase);
            var config = await _configManager.GetAsync(_configId);
            config.Should().Be(configFromDatabase);
        }
        
        [Test]
        public async Task given_store_config_in_database_equal_false_config_should_not_be_updated()
        {
            _defaultConfig.StoreConfigInDatabase = false;
            await _configManager.UpdateAsync(_defaultConfig);
            await _configRepository.DidNotReceiveWithAnyArgs().GetAsync(Arg.Any<string>());
            await _configRepository.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<NdmConfig>());
            await _configRepository.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<NdmConfig>());
        }
        
        [Test]
        public async Task given_store_config_in_database_equal_true_and_no_previous_config_it_should_be_added()
        {
            _defaultConfig.StoreConfigInDatabase = true;
            var config = new NdmConfig();
            await _configManager.UpdateAsync(config);
            await _configRepository.Received().AddAsync(config);
            await _configRepository.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<NdmConfig>());
        }
        
        [Test]
        public async Task given_store_config_in_database_equal_true_and_no_previous_config_it_should_be_updated()
        {
            _defaultConfig.StoreConfigInDatabase = true;
            var config = new NdmConfig();
            var configFromDatabase = new NdmConfig();
            _configRepository.GetAsync(_configId).Returns(configFromDatabase);
            await _configManager.UpdateAsync(config);
            await _configRepository.Received().UpdateAsync(config);
            await _configRepository.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<NdmConfig>());
        }
    }
}