// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Network.IP
{
    class WebIPSource : IIPSource
    {
        private readonly string _url;
        private readonly ILogger _logger;

        public WebIPSource(string url, ILogManager logManager)
        {
            _url = url;
            _logger = logManager.GetClassLogger<WebIPSource>();
        }

        public Task<(bool, IPAddress)> TryGetIP()
        {
            try
            {
                using HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
                if (_logger.IsInfo) _logger.Info($"Using {_url} to get external ip");
                string ip = httpClient.GetStringAsync(_url).Result.Trim();
                if (_logger.IsDebug) _logger.Debug($"External ip: {ip}");
                bool result = IPAddress.TryParse(ip, out IPAddress ipAddress);
                return Task.FromResult(result && !ipAddress.IsInternal() ? (true, ipAddress) : (false, (IPAddress)null));
            }
            catch (Exception e)
            {
                if (_logger.IsDebug) _logger.Error($"Error while getting external ip from {_url}", e);
                return Task.FromResult((false, (IPAddress)null));
            }
        }
    }
}
