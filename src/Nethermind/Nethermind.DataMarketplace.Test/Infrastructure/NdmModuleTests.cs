// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
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
        private INdmModule _ndmModule;
        private INdmApi _ndmApi;

        [SetUp]
        public void Setup()
        {
            _ndmApi = new NdmApi(Substitute.For<INethermindApi>());
            _ndmModule = new NdmModule(_ndmApi);
        }

        [Test]
        public async Task init_should_return_services()
        {
            _ndmApi.HttpClient = Substitute.For<IHttpClient>();
            _ndmApi.ConfigManager = Substitute.For<IConfigManager>();
            _ndmApi.NdmConfig = new NdmConfig();
            await _ndmModule.InitAsync();
        }
    }
}
