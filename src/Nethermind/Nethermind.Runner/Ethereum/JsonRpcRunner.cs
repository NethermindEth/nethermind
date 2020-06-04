﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Runner.LogBridge;
using Nethermind.WebSockets;
using ILogger = Nethermind.Logging.ILogger;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Nethermind.Runner.Ethereum
{
    public class JsonRpcRunner : IRunner
    {
        private readonly ILogger _logger;
        private readonly IConfigProvider _configurationProvider;
        private readonly IRpcModuleProvider _moduleProvider;
        private readonly ILogManager _logManager;
        private readonly IJsonRpcProcessor _jsonRpcProcessor;
        private readonly IWebSocketsManager _webSocketsManager;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private IWebHost? _webHost;
        private IInitConfig _initConfig;

        public JsonRpcRunner(
            IConfigProvider configurationProvider,
            IRpcModuleProvider moduleProvider,
            ILogManager logManager,
            IJsonRpcProcessor jsonRpcProcessor,
            IWebSocketsManager webSocketsManager)
        {
            _jsonRpcConfig = configurationProvider.GetConfig<IJsonRpcConfig>();
            _initConfig = configurationProvider.GetConfig<IInitConfig>();
            _configurationProvider = configurationProvider;
            _moduleProvider = moduleProvider ?? throw new ArgumentNullException(nameof(moduleProvider));
            _logManager = logManager;
            _jsonRpcProcessor = jsonRpcProcessor;
            _webSocketsManager = webSocketsManager;
            _logger = logManager.GetClassLogger();
        }

        public Task Start(CancellationToken cancellationToken)
        {
            IEnumerable<string> GetUrls()
            {
                const string nethermindUrlVariable = "NETHERMIND_URL";
                string host = _jsonRpcConfig.Host;
                string scheme = "http";
                var defaultUrl = $"{scheme}://{host}:{_jsonRpcConfig.Port}";
                var urlVariable = Environment.GetEnvironmentVariable(nethermindUrlVariable);
                string url = defaultUrl;

                if (!string.IsNullOrWhiteSpace(urlVariable))
                {
                    if (Uri.TryCreate(urlVariable, UriKind.Absolute, out var uri))
                    {
                        url = urlVariable;
                        host = uri.Host;
                        scheme = uri.Scheme;
                    }
                    else
                    {
                        if (_logger.IsWarn) _logger.Warn($"Environment variable '{nethermindUrlVariable}' value '{urlVariable}' is not valid JSON RPC URL, using default url : '{defaultUrl}'");
                    }
                }
                
                yield return url;

                if (_initConfig.WebSocketsEnabled && _jsonRpcConfig.WebSocketsPort != _jsonRpcConfig.Port)
                {
                    yield return  $"{scheme}://{host}:{_jsonRpcConfig.WebSocketsPort}";
                }
            }

            if (_logger.IsDebug) _logger.Debug("Initializing JSON RPC");
            var urls = GetUrls().ToArray();
            var webHost = WebHost.CreateDefaultBuilder()
                .ConfigureServices(s =>
                {
                    s.AddSingleton(_configurationProvider);
                    s.AddSingleton(_jsonRpcProcessor);
                    s.AddSingleton(_webSocketsManager);
                })
                .UseStartup<Startup>()
                .UseUrls(urls)
                .ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Information);
                    logging.ClearProviders();
                    logging.AddProvider(new CustomMicrosoftLoggerProvider(_logManager));
                })
                .Build();
            
            string urlsString = string.Join(" ; ", urls);
            
            ThisNodeInfo.AddInfo("JSON RPC     :", $"{urlsString}");
            ThisNodeInfo.AddInfo("RPC modules  :", $"{string.Join(", ", _moduleProvider.Enabled.OrderBy(x => x))}");
            
            _webHost = webHost;

            if (!cancellationToken.IsCancellationRequested)
            {
                _webHost.Start();
                if (_logger.IsDebug) _logger.Debug($"JSON RPC     : {urlsString}");
                if (_logger.IsDebug) _logger.Debug($"RPC modules  : {string.Join(", ", _moduleProvider.Enabled.OrderBy(x => x))}");
            }

            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            try
            {
                await (_webHost?.StopAsync() ?? Task.CompletedTask);
                if(_logger.IsInfo) _logger.Info("JSON RPC service stopped");
            }
            catch (Exception e)
            {
                if(_logger.IsInfo) _logger.Info($"Error when stopping JSON RPC service: {e}");
            }
        }
    }
}