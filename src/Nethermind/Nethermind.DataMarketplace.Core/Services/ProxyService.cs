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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class ProxyService : IProxyService
    {
        private readonly IJsonRpcClientProxy? _jsonRpcClientProxy;
        private readonly IConfigManager _configManager;
        private readonly string _configId;
        private readonly ILogger _logger;

        public ProxyService(IJsonRpcClientProxy? jsonRpcClientProxy, IConfigManager configManager, string configId,
            ILogManager logManager)
        {
            _jsonRpcClientProxy = jsonRpcClientProxy;
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _configId = configId ?? throw new ArgumentNullException(nameof(configId));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task<NdmProxy?> GetAsync()
        {
            NdmConfig? config = await _configManager.GetAsync(_configId);
            return new NdmProxy(config?.ProxyEnabled ?? false, config?.JsonRpcUrlProxies ?? Enumerable.Empty<string>());
        }

        public async Task SetAsync(IEnumerable<string> urls)
        {
            var providedUrls = urls?.ToArray() ?? Array.Empty<string>();
            _jsonRpcClientProxy?.SetUrls(providedUrls);
            NdmConfig? config = await _configManager.GetAsync(_configId);
            if (config == null)
            {
                if(_logger.IsError) _logger.Error($"Failed to retrieve config {_configId} to update JSON RPC procy");
                throw new InvalidOperationException($"Failed to retrieve config {_configId} to update JSON RPC procy");
            }
            else
            {
                config.JsonRpcUrlProxies = providedUrls;
                await _configManager.UpdateAsync(config);
                if (_logger.IsInfo) _logger.Info("Updated JSON RPC Proxy configuration.");   
            }
        }
    }
}