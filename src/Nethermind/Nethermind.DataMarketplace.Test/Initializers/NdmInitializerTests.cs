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

using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Modules;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.Logging;
using Nethermind.Runner;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Initializers
{
    public class NdmInitializerTests
    {
        private INdmModule _ndmModule;
        private INdmConsumersModule _ndmConsumersModule;
        private IConfigProvider _configProvider;
        private bool _enableUnsecuredDevWallet;
        private NdmConfig _ndmConfig;
        private InitConfig _initConfig;
        private NdmInitializer _ndmInitializer;

        [SetUp]
        public void Setup()
        {
            _ndmModule = Substitute.For<INdmModule>();
            _ndmConsumersModule = Substitute.For<INdmConsumersModule>();
            _configProvider = Substitute.For<IConfigProvider>();
            _enableUnsecuredDevWallet = false;
            _ndmConfig = new NdmConfig {Enabled = true, StoreConfigInDatabase = false};
            _initConfig = new InitConfig();
            _configProvider.GetConfig<INdmConfig>().Returns(_ndmConfig);
            _ndmInitializer = new NdmInitializer(_ndmModule, _ndmConsumersModule, LimboLogs.Instance);
        }

        [Test]
        public async Task database_path_should_be_base_db_and_ndm_db_path()
        {
            _initConfig.BaseDbPath = "db";
            _ndmConfig.DatabasePath = "ndm";
            INethermindApi nethermindApi = Substitute.For<INethermindApi>();
            INdmApi ndmApi = new NdmApi(nethermindApi);
            var configProvider = Substitute.For<IConfigProvider>();
            ndmApi.ConfigProvider = configProvider;
            configProvider.GetConfig<INdmConfig>().Returns(_ndmConfig);
            configProvider.GetConfig<IInitConfig>().Returns(_initConfig);
            ndmApi.AccountService = Substitute.For<IAccountService>();

            await _ndmInitializer.InitAsync(ndmApi);
            _ndmInitializer.DbPath.Should().Be(Path.Combine(_initConfig.BaseDbPath, _ndmConfig.DatabasePath));
        }
    }
}