// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Runner.JsonRpc;
using Nethermind.Runner.Logging;
using Nethermind.Sockets;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using WebHost = Nethermind.Runner.JsonRpc.WebHost;

namespace Nethermind.Runner.Ethereum
{
    public class JsonRpcRunner
    {
        private readonly Nethermind.Logging.ILogger _logger;
        private readonly IConfigProvider _configurationProvider;
        private readonly IRpcAuthentication _rpcAuthentication;
        private readonly ILogManager _logManager;
        private readonly IJsonRpcProcessor _jsonRpcProcessor;
        private readonly IJsonRpcUrlCollection _jsonRpcUrlCollection;
        private readonly IWebSocketsManager _webSocketsManager;
        private WebHost? _webApp;
        private readonly IJsonRpcServiceConfigurer[] _jsonRpcServices;
        private readonly ITxPool _txPool;
        private readonly ISpecProvider _specProvider;
        private readonly IReceiptFinder _receiptFinder;
        private readonly IBlockTree _blockTree;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly IMainProcessingContext _mainProcessingContext;

        public JsonRpcRunner(
            IJsonRpcProcessor jsonRpcProcessor,
            IJsonRpcUrlCollection jsonRpcUrlCollection,
            IWebSocketsManager webSocketsManager,
            IConfigProvider configurationProvider,
            IRpcAuthentication rpcAuthentication,
            ILogManager logManager,
            IJsonRpcServiceConfigurer[] jsonRpcServices,
            ITxPool txPool,
            ISpecProvider specProvider,
            IReceiptFinder receiptFinder,
            IBlockTree blockTree,
            ISyncPeerPool syncPeerPool,
            IMainProcessingContext mainProcessingContext)
        {
            _configurationProvider = configurationProvider;
            _rpcAuthentication = rpcAuthentication;
            _jsonRpcUrlCollection = jsonRpcUrlCollection;
            _logManager = logManager;
            _jsonRpcProcessor = jsonRpcProcessor;
            _webSocketsManager = webSocketsManager;
            _jsonRpcServices = jsonRpcServices;
            _logger = logManager.GetClassLogger();
            _txPool = txPool;
            _specProvider = specProvider;
            _receiptFinder = receiptFinder;
            _blockTree = blockTree;
            _syncPeerPool = syncPeerPool;
            _mainProcessingContext = mainProcessingContext;
        }

        public async Task Start(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug) _logger.Debug("Initializing JSON RPC");
            string[] urls = _jsonRpcUrlCollection.Urls;
            WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ApplicationName = "Nethermind"
            });

            IServiceCollection services = builder.Services;
            services.AddSingleton<DiagnosticListener>(NullDiagnosticListener.Instance);
            services.AddSingleton<DiagnosticSource>(NullDiagnosticListener.Instance);

            Startup startup = new();
            builder.WebHost
                // Explicitly build from UseKestrelCore rather than UseKestrel to
                // not add additional transports that we don't use e.g. msquic as that
                // adds a lot of additional idle threads to the process.
                .UseKestrelCore()
                .UseKestrelHttpsConfiguration()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton(_configurationProvider);
                    s.AddSingleton(_jsonRpcProcessor);
                    s.AddSingleton(_jsonRpcUrlCollection);
                    s.AddSingleton(_webSocketsManager);
                    s.AddSingleton(_rpcAuthentication);
                    s.AddSingleton(_txPool);
                    s.AddSingleton(_specProvider);
                    s.AddSingleton(_receiptFinder);
                    s.AddSingleton(_blockTree);
                    s.AddSingleton(_syncPeerPool);
                    s.AddSingleton(_mainProcessingContext);
                    foreach (IJsonRpcServiceConfigurer configurer in _jsonRpcServices)
                    {
                        configurer.Configure(s);
                    }
                    s.AddSingleton<ApplicationLifetime>();
                    s.Configure<KestrelServerOptions>(options =>
                    {
                        options.AllowSynchronousIO = true;
                    });

                    startup.ConfigureServices(s);
                })
                .UseUrls(urls)
                .ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Information);
                    logging.ClearProviders();
                    logging.AddProvider(new CustomMicrosoftLoggerProvider(_logManager));
                    logging.Configure(options =>
                        options.ActivityTrackingOptions = ActivityTrackingOptions.None);
                });

            WebApplication webApp = builder.Build();

            string urlsString = string.Join(" ; ", urls);
            // TODO: replace http with ws where relevant

            ThisNodeInfo.AddInfo("JSON RPC     :", $"{urlsString}");

            _webApp = new WebHost(webApp.Services, webApp.Configuration, startup, _logManager);

            if (!cancellationToken.IsCancellationRequested)
            {
                await NetworkHelper.HandlePortTakenError(
                    () => _webApp.StartAsync(cancellationToken), urls
                );
                if (_logger.IsDebug) _logger.Debug($"JSON RPC     : {urlsString}");
            }
        }

        public async Task StopAsync()
        {
            try
            {
                await (_webApp?.StopAsync() ?? Task.CompletedTask);
                if (_logger.IsInfo) _logger.Info("JSON RPC service stopped");
            }
            catch (Exception e)
            {
                if (_logger.IsInfo) _logger.Info($"Error when stopping JSON RPC service: {e}");
            }
        }
    }
}
