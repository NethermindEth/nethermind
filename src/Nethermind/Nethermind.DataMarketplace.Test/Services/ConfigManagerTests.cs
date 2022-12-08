// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
