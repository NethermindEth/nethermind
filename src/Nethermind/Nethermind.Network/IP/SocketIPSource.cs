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

using System;
using System.Net;
using System.Net.Sockets;
using Nethermind.Logging;

namespace Nethermind.Network.IP
{
    public class SocketIPSource : IIPSource
    {
        private readonly ILogger _logger;

        public SocketIPSource(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger<SocketIPSource>();
        }
        
        public bool TryGetIP(out IPAddress ipAddress)
        {
            try
            {
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("www.google.com", 80);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                ipAddress = endPoint?.Address;
                if (_logger.IsDebug) _logger.Debug($"Local ip: {ipAddress}");
                return ipAddress != null;
            }
            catch (SocketException e)
            {
                if(_logger.IsError) _logger.Error($"Error while getting local ip from socket.", e);
                ipAddress = null;
                return false;
            }
        }
    }
}