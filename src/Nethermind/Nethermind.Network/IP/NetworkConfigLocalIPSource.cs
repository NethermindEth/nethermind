// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            if (_config.LocalIp is not null)
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
