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

using Nethermind.Api;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Modules;
using Nethermind.Facade.Proxy;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Infrastructure
{
    public class NdmModuleTests
    {
        private string _baseDbPath;
        private bool _enableUnsecuredDevWallet;
        private INdmModule _ndmModule;

        [SetUp]
        public void Setup()
        {
            _baseDbPath = "db";
            _enableUnsecuredDevWallet = false;
            _ndmModule = new NdmModule();
        }

        [Test]
        public void init_should_return_services()
        {
            NdmApi ndmApi = new NdmApi(Substitute.For<INethermindApi>());
            ndmApi.HttpClient = Substitute.For<IHttpClient>();
            ndmApi.ConfigManager = Substitute.For<IConfigManager>();
            ndmApi.NdmConfig = new NdmConfig();
            _ndmModule.Init(ndmApi);
        }
    }
}