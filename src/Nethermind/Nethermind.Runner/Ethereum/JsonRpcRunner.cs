// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Runner.JsonRpc;
using Nethermind.Runner.Logging;
using Nethermind.Sockets;
using ILogger = Nethermind.Logging.ILogger;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Nethermind.Runner.Ethereum
{
    public class JsonRpcRunner
    {
        private readonly ILogger _logger;
        private readonly IConfigProvider _configurationProvider;
        private readonly IRpcAuthentication _rpcAuthentication;
        private readonly ILogManager _logManager;
        private readonly IJsonRpcProcessor _jsonRpcProcessor;
        private readonly IJsonRpcUrlCollection _jsonRpcUrlCollection;
        private readonly IWebSocketsManager _webSocketsManager;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private IWebHost? _webHost;
        private IInitConfig _initConfig;
        private INethermindApi _api;

        public JsonRpcRunner(
            IJsonRpcProcessor jsonRpcProcessor,
            IJsonRpcUrlCollection jsonRpcUrlCollection,
            IWebSocketsManager webSocketsManager,
            IConfigProvider configurationProvider,
            IRpcAuthentication rpcAuthentication,
            ILogManager logManager,
            INethermindApi api)
        {
            _jsonRpcConfig = configurationProvider.GetConfig<IJsonRpcConfig>();
            _initConfig = configurationProvider.GetConfig<IInitConfig>();
            _configurationProvider = configurationProvider;
            _rpcAuthentication = rpcAuthentication;
            _jsonRpcUrlCollection = jsonRpcUrlCollection;
            _logManager = logManager;
            _jsonRpcProcessor = jsonRpcProcessor;
            _webSocketsManager = webSocketsManager;
            _logger = logManager.GetClassLogger();
            _api = api;
        }

        public Task Start(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug) _logger.Debug("Initializing JSON RPC");
            string[] urls = _jsonRpcUrlCollection.Urls;
            var webHost = WebHost.CreateDefaultBuilder()
                .ConfigureServices(s =>
                {
                    s.AddSingleton(_configurationProvider);
                    s.AddSingleton(_jsonRpcProcessor);
                    s.AddSingleton(_jsonRpcUrlCollection);
                    s.AddSingleton(_webSocketsManager);
                    s.AddSingleton(_rpcAuthentication);
                    foreach (var plugin in _api.Plugins.OfType<INethermindServicesPlugin>())
                    {
                        plugin.AddServices(s);
                    };
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
            // TODO: replace http with ws where relevant

            ThisNodeInfo.AddInfo("JSON RPC     :", $"{urlsString}");

            _webHost = webHost;

            if (!cancellationToken.IsCancellationRequested)
            {
                _webHost.Start();
                if (_logger.IsDebug) _logger.Debug($"JSON RPC     : {urlsString}");
            }

            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            try
            {
                await (_webHost?.StopAsync() ?? Task.CompletedTask);
                if (_logger.IsInfo) _logger.Info("JSON RPC service stopped");
            }
            catch (Exception e)
            {
                if (_logger.IsInfo) _logger.Info($"Error when stopping JSON RPC service: {e}");
            }
        }
    }
}
