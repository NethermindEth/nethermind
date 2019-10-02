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

using System;
using System.Net;
using System.Net.Sockets;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Network
{
    public class IpResolver : IIpResolver
    {
        private ILogger _logger;
        private INetworkConfig _networkConfig;

        public IpResolver(INetworkConfig networkConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            
            LocalIp = InitializeLocalIp();
            ExternalIp = InitializeExternalIp();
        }
        
        public IPAddress LocalIp { get; }

        public IPAddress ExternalIp { get; }

        private IPAddress InitializeExternalIp()
        {
            string externalIpSetInEnv = Environment.GetEnvironmentVariable("NETHERMIND_ENODE_IPADDRESS");
            if (externalIpSetInEnv != null)
            {
                return IPAddress.Parse(externalIpSetInEnv);
            }
            
            if (_networkConfig.ExternalIp != null)
            {
                if(_logger.IsWarn) _logger.Warn($"Using the external IP override: {nameof(NetworkConfig)}.{nameof(NetworkConfig.ExternalIp)} = {_networkConfig.ExternalIp}");
                return IPAddress.Parse(_networkConfig.ExternalIp);
            }
            
            try
            {
                const string url = "http://checkip.amazonaws.com";
                if(_logger.IsInfo) _logger.Info($"Using {url} to get external ip");
                string ip = new WebClient().DownloadString(url);
                if(_logger.IsInfo) _logger.Info($"External ip: {ip}");
                return IPAddress.Parse(ip.Trim());
            }
            catch (Exception e)
            {
                if(_logger.IsError) _logger.Error("Error while getting external ip", e);
                return IPAddress.Loopback;
            }
        }

        private IPAddress InitializeLocalIp()
        {
            if (_networkConfig.LocalIp != null)
            {
                if(_logger.IsWarn) _logger.Warn($"Using the local IP override: {nameof(NetworkConfig)}.{nameof(NetworkConfig.LocalIp)} = {_networkConfig.LocalIp}");
                return IPAddress.Parse(_networkConfig.LocalIp);
            }
            
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("www.google.com", 80);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    var address = endPoint?.Address;
                    if(_logger.IsDebug) _logger.Debug($"Local ip: {address}");
                    return address;
                }
            }
            catch (Exception e)
            {
                if(_logger.IsError) _logger.Error("Error while getting local ip", e);
                return null;
            }
        }
    }
}