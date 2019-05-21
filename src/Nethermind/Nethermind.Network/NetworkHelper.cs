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

namespace Nethermind.Network
{
    public class NetworkHelper : INetworkHelper
    {
        private readonly ILogger _logger;
        private IPAddress _localIp;
        private IPAddress _externalIp;

        public NetworkHelper(ILogger logger)
        {
            _logger = logger;
        }

        public IPAddress GetLocalIp()
        {
            return _localIp ?? (_localIp = FindLocalIp());
        }

        public IPAddress GetExternalIp()
        {
            return _externalIp ?? (_externalIp = FindExternalIp());
        }

        private IPAddress FindExternalIp()
        {
            try
            {
                var url = "http://checkip.amazonaws.com";
                _logger.Info($"Using {url} to get external ip");
                var ip = new WebClient().DownloadString(url);
                _logger.Info($"External ip: {ip}");
                return IPAddress.Parse(ip?.Trim());
            }
            catch (Exception e)
            {
                _logger.Error("Error while getting external ip", e);
                return null;
            }
        }

        private IPAddress FindLocalIp()
        {
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
                _logger.Error("Error while getting local ip", e);
                return null;
            }
        }
    }
}