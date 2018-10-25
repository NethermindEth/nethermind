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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Nethermind.Config;
using Log = Nethermind.Core.Logging;
using Nethermind.JsonRpc.Config;
using Nethermind.JsonRpc.DataModel;
using Nethermind.Runner.Config;
using Microsoft.Extensions.Logging;
using Nethermind.Runner.LogBridge;

namespace Nethermind.Runner.Runners
{
    public class JsonRpcRunner : IJsonRpcRunner
    {
        private readonly Log.ILogger _logger;
        private readonly Log.ILogManager _logManager;
        private readonly IJsonRpcConfig _configurationProvider;
        private readonly IInitConfig _initConfig;
        private IWebHost _webHost;

        public JsonRpcRunner(IConfigProvider configurationProvider, Log.ILogManager logManager)
        {
            _configurationProvider = configurationProvider.GetConfig<IJsonRpcConfig>();
            _initConfig = configurationProvider.GetConfig<IInitConfig>();
            _logManager = logManager;
            _logger = logManager.GetClassLogger();
        }

        public Task Start()
        {
            _logger.Info("Initializing JsonRPC");
            var host = $"http://{_initConfig.HttpHost}:{_initConfig.HttpPort}";
            _logger.Info($"Running server, url: {host}");

            var webHost = WebHost.CreateDefaultBuilder()
                .UseStartup<Startup>()
                .UseUrls(host)
                .ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Information);
                    logging.ClearProviders();
                    logging.AddProvider(new CustomMicrosoftLoggerProvider(_logManager));
                })
                .Build();

            var modules = GetModules(_initConfig.JsonRpcEnabledModules)?.ToList();
            if (modules != null && modules.Any())
            {
                _configurationProvider.EnabledModules = modules;
            }

            _logger.Info($"Starting http service, modules: {string.Join(", ", _configurationProvider.EnabledModules.Select(x => x))}");
            _webHost = webHost;
            _webHost.Start();
            _logger.Info("JsonRPC initialization completed");

            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            try
            {
                await _webHost.StopAsync();
                _logger.Info("Service stopped");
            }
            catch (Exception e)
            {
                _logger.Info($"Error during stopping service: {e}");
            }
        }

        private IEnumerable<ModuleType> GetModules(string[] moduleNames)
        {
            if (moduleNames == null || !moduleNames.Any())
            {
                return null;
            }

            var modules = new List<ModuleType>();
            foreach (var moduleName in moduleNames)
            {
                if (Enum.TryParse(moduleName.Trim(), true, out ModuleType moduleType))
                {
                    modules.Add(moduleType);
                }
                else
                {
                    _logger.Warn($"Incorrect jsonRpc module type: {moduleName}");
                }
            }

            return modules;
        }
    }
}