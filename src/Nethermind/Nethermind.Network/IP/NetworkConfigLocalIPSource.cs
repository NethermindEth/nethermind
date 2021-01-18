//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System.Net;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Network.IP
{
    class NetworkConfigLocalIPSource : IIPSource
    {
        private readonly INetworkConfig _config;
        private readonly ILogger _logger;

        public NetworkConfigLocalIPSource(INetworkConfig config, ILogManager logManager)
        {
            _config = config;
            _logger = logManager.GetClassLogger<NetworkConfigLocalIPSource>();
        }
        
        public Task<(bool, IPAddress)> TryGetIP()
        {
            if (_config.LocalIp != null)
            {
                bool result = IPAddress.TryParse(_config.LocalIp, out IPAddress ipAddress);
                
                if (result)
                {
                    if (_logger.IsWarn) _logger.Warn($"Using the local IP override: {nameof(NetworkConfig)}.{nameof(NetworkConfig.LocalIp)} = {_config.LocalIp}");
                }
                else
                {
                    if (_logger.IsWarn) _logger.Warn($"Local IP override: {nameof(NetworkConfig)}.{nameof(NetworkConfig.LocalIp)} = {_config.LocalIp} has incorrect format.");
                }

                return Task.FromResult((result, ipAddress));
            }
            
            return Task.FromResult((false, (IPAddress)null));
        }
    }
}
