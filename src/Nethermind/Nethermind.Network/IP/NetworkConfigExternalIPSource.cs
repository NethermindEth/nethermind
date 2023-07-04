// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Network.IP
{
    class NetworkConfigExternalIPSource : IIPSource
    {
        private readonly INetworkConfig _config;
        private readonly ILogger _logger;

        public NetworkConfigExternalIPSource(INetworkConfig config, ILogManager logManager)
        {
            _config = config;
            _logger = logManager.GetClassLogger<NetworkConfigExternalIPSource>();
        }

        public Task<(bool, IPAddress)> TryGetIP()
        {
            if (_config.ExternalIp is not null)
            {
                bool result = IPAddress.TryParse(_config.ExternalIp, out IPAddress ipAddress);

                if (result)
                {
                    if (_logger.IsWarn) _logger.Warn($"Using the external IP override: {nameof(NetworkConfig)}.{nameof(NetworkConfig.ExternalIp)} = {_config.ExternalIp}");
                }
                else
                {
                    if (_logger.IsWarn) _logger.Warn($"External IP override: {nameof(NetworkConfig)}.{nameof(NetworkConfig.ExternalIp)} = {_config.ExternalIp} has incorrect format.");
                }

                return Task.FromResult((result, ipAddress));
            }

            return Task.FromResult((false, (IPAddress)null));
        }
    }
}
