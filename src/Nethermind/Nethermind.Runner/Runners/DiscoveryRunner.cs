/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Discovery;
using Nethermind.Discovery.Serializers;

namespace Nethermind.Runner.Runners
{
    public class DiscoveryRunner : IDiscoveryRunner
    {
        private readonly ILogger _logger;
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly IDiscoveryMsgSerializersProvider _discoveryMsgSerializersProvider;
        private readonly IDiscoveryApp _discoveryApp;
        private readonly IPrivateKeyProvider _privateKeyProvider;

        public DiscoveryRunner(IDiscoveryMsgSerializersProvider discoveryMsgSerializersProvider, IDiscoveryApp discoveryApp, IDiscoveryConfigurationProvider configurationProvider, IPrivateKeyProvider privateKeyProvider, ILogger logger)
        {
            _discoveryMsgSerializersProvider = discoveryMsgSerializersProvider;
            _discoveryApp = discoveryApp;
            _configurationProvider = configurationProvider;
            _privateKeyProvider = privateKeyProvider;
            _logger = logger;
        }

        public void Start(InitParams initParams)
        {
            _logger.Info("Initializing Discovery");
            if (initParams.DiscoveryPort.HasValue)
            {
                _configurationProvider.MasterPort = initParams.DiscoveryPort.Value;
            }
            
            _discoveryMsgSerializersProvider.RegisterDiscoverySerializers();
            _discoveryApp.Start(new PrivateKey(initParams.TestNodeKey).PublicKey);
            _logger.Info("Discovery initialization completed");
        }

        public async Task StopAsync()
        {
            await _discoveryApp.StopAsync();
        }
    }
}