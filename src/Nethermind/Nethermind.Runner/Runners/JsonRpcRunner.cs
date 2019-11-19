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
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Config;
using Microsoft.Extensions.Logging;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Runner.LogBridge;
using Nethermind.WebSockets;
using ILogger = Nethermind.Logging.ILogger;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Nethermind.Runner.Runners
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
        private IWebHost _webHost;

        public JsonRpcRunner(IConfigProvider configurationProvider, IRpcModuleProvider moduleProvider,
            ILogManager logManager, IJsonRpcProcessor jsonRpcProcessor, IWebSocketsManager webSocketsManager)
        {
            _jsonRpcConfig = configurationProvider.GetConfig<IJsonRpcConfig>();
            _configurationProvider = configurationProvider;
            _moduleProvider = moduleProvider ?? throw new ArgumentNullException(nameof(moduleProvider));
            _logManager = logManager;
            _jsonRpcProcessor = jsonRpcProcessor;
            _webSocketsManager = webSocketsManager;
            _logger = logManager.GetClassLogger();
        }

        public Task Start()
        {
            if (_logger.IsDebug) _logger.Debug("Initializing JSON RPC");
            var hostVariable = Environment.GetEnvironmentVariable("NETHERMIND_URL");
            var host = string.IsNullOrWhiteSpace(hostVariable)
                ? $"http://{_jsonRpcConfig.Host}:{_jsonRpcConfig.Port}"
                : hostVariable;
            if (_logger.IsInfo) _logger.Info($"Running server, url: {host}");
            var webHost = WebHost.CreateDefaultBuilder()
                .ConfigureServices(s =>
                {
                    s.AddSingleton(_configurationProvider);
                    s.AddSingleton(_jsonRpcProcessor);
                    s.AddSingleton(_webSocketsManager);
                })
                .UseStartup<Startup>()
                .UseUrls(host)
                .ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Information);
                    logging.ClearProviders();
                    logging.AddProvider(new CustomMicrosoftLoggerProvider(_logManager));
                })
                .Build();

            _webHost = webHost;
            _webHost.Start();
            if (_logger.IsInfo) _logger.Info($"JSON RPC     : {host}");
            if (_logger.IsInfo) _logger.Info($"RPC modules  : {string.Join(", ", _moduleProvider.Enabled.OrderBy(x => x))}");
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            try
            {
                await _webHost.StopAsync();
                if(_logger.IsInfo) _logger.Info("JSON RPC service stopped");
            }
            catch (Exception e)
            {
                if(_logger.IsInfo) _logger.Info($"Error when stopping JSON RPC service: {e}");
            }
        }
    }
}