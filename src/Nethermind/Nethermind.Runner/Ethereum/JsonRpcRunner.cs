// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
    public class JsonRpcRunner(
        JsonRpcProcessor jsonRpcProcessor,
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
        private readonly JsonRpcProcessor _jsonRpcProcessor = jsonRpcProcessor;
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
                    startup.ConfigureServices(s);
                })
                .UseUrls(GetListenUrls(_jsonRpcUrlCollection.Values, Dns.GetHostAddresses))
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
                // #13203: the first point at which the ports actually accept a request. The engine URL is in the
                // same set, so this is also when the consensus client can connect. At Debug there was nothing to
                // distinguish a node still gated behind startup work from one already serving.
                if (_logger.IsInfo) _logger.Info($"JSON-RPC is listening on {urlsString}");
            }
        }

        /// <summary>
        /// Builds the addresses Kestrel binds to, replacing each host name with the IP addresses it resolves to.
        /// </summary>
        /// <remarks>
        /// Kestrel binds any host that is neither an IP literal nor <c>localhost</c> to all interfaces,
        /// so a host name such as <c>node.lan</c> would otherwise expose the port on every address of the machine.
        /// </remarks>
        /// <exception cref="InvalidOperationException">A host name resolves to no addresses.</exception>
        internal static string[] GetListenUrls(IEnumerable<JsonRpcUrl> urls, Func<string, IPAddress[]> resolveHost)
        {
            List<string> listenUrls = [];
            foreach (JsonRpcUrl url in urls)
            {
                if (IPAddress.TryParse(url.Host, out _) ||
                    url.Host is "*" or "+" ||
                    string.Equals(url.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    listenUrls.Add(url.ToString());
                    continue;
                }

                IPAddress[] addresses = resolveHost(url.Host);
                if (addresses.Length == 0)
                {
                    throw new InvalidOperationException($"JSON RPC host '{url.Host}' does not resolve to any IP address");
                }

                foreach (IPAddress address in addresses)
                {
                    string host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
                    string listenUrl = $"{url.Scheme}://{host}:{url.Port}";
                    if (!listenUrls.Contains(listenUrl))
                    {
                        listenUrls.Add(listenUrl);
                    }
                }
            }

            return listenUrls.ToArray();
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
