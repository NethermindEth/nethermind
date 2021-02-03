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
                HttpClient httpClient = new HttpClient {Timeout = TimeSpan.FromSeconds(3)};
                if(_logger.IsInfo) _logger.Info($"Using {_url} to get external ip");
                string ip = httpClient.GetStringAsync(_url).Result.Trim();
                if(_logger.IsDebug) _logger.Debug($"External ip: {ip}");
                bool result = IPAddress.TryParse(ip, out IPAddress ipAddress);
                return Task.FromResult(result && !ipAddress.IsInternal() ? (true, ipAddress) : (false, (IPAddress)null));
            }
            catch (Exception e)
            {
                if(_logger.IsDebug) _logger.Error($"Error while getting external ip from {_url}", e);
                return Task.FromResult((false, (IPAddress)null));
            }
        }
    }
}
