// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.IP;

namespace Nethermind.Network
{
    public class IPResolver : IIPResolver
    {
        private readonly ILogger _logger;
        private readonly INetworkConfig _networkConfig;
        private readonly ILogManager _logManager;

        public IPResolver(INetworkConfig networkConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _logManager = logManager;
        }

        public async Task Initialize()
        {
            try
            {
                LocalIp = await InitializeLocalIp();
            }
            catch (Exception)
            {
                LocalIp = IPAddress.Loopback;
            }

            try
            {
                ExternalIp = await InitializeExternalIp();
            }
            catch (Exception)
            {
                ExternalIp = IPAddress.None;
            }
        }

        public IPAddress LocalIp { get; private set; }

        public IPAddress ExternalIp { get; private set; }

        private async Task<IPAddress> InitializeExternalIp()
        {
            IEnumerable<IIPSource> GetIPSources()
            {
                yield return new EnvironmentVariableIPSource();
                yield return new NetworkConfigExternalIPSource(_networkConfig, _logManager);
                yield return new WebIPSource("http://ipv4.icanhazip.com", _logManager);
                yield return new WebIPSource("http://ipv4bot.whatismyipaddress.com", _logManager);
                yield return new WebIPSource("http://checkip.amazonaws.com", _logManager);
                yield return new WebIPSource("http://ipinfo.io/ip", _logManager);
                yield return new WebIPSource("http://api.ipify.org", _logManager);
            }

            try
            {
                foreach (IIPSource s in GetIPSources())
                {
                    (bool success, IPAddress ip) = await s.TryGetIP();
                    if (success)
                    {
                        ThisNodeInfo.AddInfo("External IP  :", $"{ip}");
                        return ip;
                    }
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error while getting external ip", e);
            }

            return IPAddress.Loopback;
        }

        private async Task<IPAddress> InitializeLocalIp()
        {
            IEnumerable<IIPSource> GetIPSources()
            {
                yield return new NetworkConfigLocalIPSource(_networkConfig, _logManager);
                yield return new SocketIPSource(_logManager);
            }

            try
            {
                foreach (var s in GetIPSources())
                {
                    (bool success, IPAddress ip) = await s.TryGetIP();
                    if (success)
                    {
                        return ip;
                    }
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error while getting local ip", e);
            }

            return IPAddress.Loopback;
        }
    }
}
