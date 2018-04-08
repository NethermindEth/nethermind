using System;
using System.Net;
using System.Net.Sockets;
using Nethermind.Core;

namespace Nethermind.Network
{
    public class NetworkHelper : INetworkHelper
    {
        private readonly ILogger _logger;

        public NetworkHelper(ILogger logger)
        {
            _logger = logger;
        }

        public IPAddress GetLocalIp()
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("www.google.com", 80);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    var address = endPoint?.Address;
                    _logger.Log($"Local ip: {address}");
                    return address;
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error while getting external ip", e);
                return null;
            }
        }

        public IPAddress GetExternalIp()
        {
            try
            {
                var url = "http://checkip.amazonaws.com";
                _logger.Log($"Using {url} to get external ip");
                var ip = new WebClient().DownloadString(url);
                _logger.Log($"External ip: {ip}");
                return IPAddress.Parse(ip?.Trim());
            }
            catch (Exception e)
            {
                _logger.Error("Error while getting external ip", e);
                return null;
            }
        }
    }
}