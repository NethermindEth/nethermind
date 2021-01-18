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
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                await socket.ConnectAsync("www.google.com", 80);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                IPAddress ipAddress = endPoint?.Address;
                if (_logger.IsDebug) _logger.Debug($"Local ip: {ipAddress}");
                return (ipAddress != null, ipAddress);
            }
            catch (SocketException)
            {
                if(_logger.IsError) _logger.Error($"Error while getting local ip from socket. You can set a manual override via config {nameof(NetworkConfig)}.{nameof(NetworkConfig.LocalIp)}");
                return (false, null);
            }
        }
    }
}
