// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Network.IP
{
    public class SocketIPSource : IIPSource
    {
        private readonly ILogger _logger;

        public SocketIPSource(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger<SocketIPSource>();
        }

        public async Task<(bool, IPAddress)> TryGetIP()
        {
            try
            {
                using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                await socket.ConnectAsync("www.google.com", 80);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                IPAddress ipAddress = endPoint?.Address;
                if (_logger.IsDebug) _logger.Debug($"Local ip: {ipAddress}");
                return (ipAddress is not null, ipAddress);
            }
            catch (SocketException)
            {
                if (_logger.IsError) _logger.Error($"Error while getting local ip from socket. You can set a manual override via config {nameof(NetworkConfig)}.{nameof(NetworkConfig.LocalIp)}");
                return (false, null);
            }
        }
    }
}
