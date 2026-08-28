// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Network.IP
{
    class WebIPSource(string url, ILogManager logManager) : IIPSource
    {
        private readonly string _url = url;
        private readonly ILogger _logger = logManager.GetClassLogger<WebIPSource>();

        public async Task<(bool, IPAddress)> TryGetIP()
        {
            try
            {
                using HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
                if (_logger.IsInfo) _logger.Info($"Using {_url} to get external ip");
                string ip = (await httpClient.GetStringAsync(_url)).Trim();
                if (_logger.IsDebug) _logger.Debug($"External ip: {ip}");
                bool result = IPAddress.TryParse(ip, out IPAddress ipAddress);
                bool isExternal = result && !ipAddress.IsLoopbackOrPrivateOrLinkLocal;
                return isExternal ? (true, ipAddress) : (false, (IPAddress)null);
            }
            catch (Exception e)
            {
                _logger.DebugError($"Error while getting external ip from {_url}", e);
                return (false, (IPAddress)null);
            }
        }
    }
}
