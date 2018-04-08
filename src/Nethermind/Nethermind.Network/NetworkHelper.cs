using System;
using System.Net;
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
            throw new NotImplementedException();
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