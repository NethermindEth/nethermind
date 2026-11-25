// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
using Nethermind.JsonRpc.Hosting;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Runner.JsonRpc;
using Nethermind.Runner.Logging;
using Nethermind.Sockets;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using WebHost = Nethermind.JsonRpc.Hosting.WebHost;

namespace Nethermind.Runner.Ethereum
{
    public class JsonRpcRunner(
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
        IMainProcessingContext mainProcessingContext) : IAsyncDisposable
    {
        private readonly Nethermind.Logging.ILogger _logger = logManager.GetClassLogger<JsonRpcRunner>();
        private readonly IConfigProvider _configurationProvider = configurationProvider;
        private readonly IRpcAuthentication _rpcAuthentication = rpcAuthentication;
        private readonly ILogManager _logManager = logManager;
        private readonly IJsonRpcProcessor _jsonRpcProcessor = jsonRpcProcessor;
        private readonly IJsonRpcUrlCollection _jsonRpcUrlCollection = jsonRpcUrlCollection;
        private readonly IWebSocketsManager _webSocketsManager = webSocketsManager;
        private WebHost? _webApp;
        private readonly IJsonRpcServiceConfigurer[] _jsonRpcServices = jsonRpcServices;
        private readonly ITxPool _txPool = txPool;
        private readonly ISpecProvider _specProvider = specProvider;
        private readonly IReceiptFinder _receiptFinder = receiptFinder;
        private readonly IBlockTree _blockTree = blockTree;
        private readonly ISyncPeerPool _syncPeerPool = syncPeerPool;
        private readonly IMainProcessingContext _mainProcessingContext = mainProcessingContext;
        private int _disposed;

        public async Task Start(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

            if (_logger.IsDebug) _logger.Debug("Initializing JSON RPC");
            string[] urls = _jsonRpcUrlCollection.Urls;
            string[] listenUrls;
            try
            {
                listenUrls = await JsonRpcListenUrls.ResolveAsync(_jsonRpcUrlCollection.Values, Dns.GetHostAddressesAsync, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (_logger.IsDebug) _logger.Debug("JSON RPC host resolution cancelled");
                return;
            }
            WebApplicationBuilder builder = JsonRpcHosting.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = "Nethermind"
            });

            Startup startup = new();
            IServiceCollection services = builder.Services;
            services.AddRouting();
            services.AddSingleton(_configurationProvider);
            services.AddSingleton(_jsonRpcProcessor);
            services.AddSingleton(_jsonRpcUrlCollection);
            services.AddSingleton(_webSocketsManager);
            services.AddSingleton(_rpcAuthentication);
            services.AddSingleton(_txPool);
            services.AddSingleton(_specProvider);
            services.AddSingleton(_receiptFinder);
            services.AddSingleton(_blockTree);
            services.AddSingleton(_syncPeerPool);
            services.AddSingleton(_mainProcessingContext);
            foreach (IJsonRpcServiceConfigurer configurer in _jsonRpcServices)
            {
                configurer.Configure(services);
            }
            services.AddSingleton<ApplicationLifetime>();
            startup.ConfigureServices(services);
            builder.WebHost
                .UseUrls(listenUrls)
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

            _webApp = new WebHost(webApp.Services, webApp.Configuration, startup, _logManager);

            if (!cancellationToken.IsCancellationRequested)
            {
                await NetworkHelper.HandlePortTakenError(
                    () => _webApp.StartAsync(cancellationToken), urls
                );
                // #13203: the first point at which the ports actually accept a request. The engine URL is in the
                // same set, so this is also when the consensus client can connect. At Debug there was nothing to
                // distinguish a node still gated behind startup work from one already serving.
                if (_logger.IsInfo) _logger.Info($"JSON-RPC is listening on {urlsString:hide}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            try
            {
                if (_webApp is not null) await _webApp.DisposeAsync();
                if (_logger.IsInfo) _logger.Info("JSON RPC service stopped");
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error when stopping JSON RPC service", e);
            }
        }
    }
}
