// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.JsonRpc;
using Nethermind.Monitoring.Config;
using Nethermind.Core.Extensions;

namespace Nethermind.HealthChecks
{
    public class HealthChecksPlugin : INethermindPlugin, INethermindServicesPlugin
    {
        private INethermindApi _api;
        private IHealthChecksConfig _healthChecksConfig;
        private INodeHealthService _nodeHealthService;
        private ILogger _logger;
        private IJsonRpcConfig _jsonRpcConfig;
        private IInitConfig _initConfig;

        private ClHealthLogger _clHealthLogger;
        private FreeDiskSpaceChecker _freeDiskSpaceChecker;

        private const int ClUnavailableReportMessageDelay = 5;

        public async ValueTask DisposeAsync()
        {
            if (_clHealthLogger is not null)
            {
                await _clHealthLogger.DisposeAsync();
            }
            if (_freeDiskSpaceChecker is not null)
            {
                await FreeDiskSpaceChecker.DisposeAsync();
            }
        }

        public string Name => "HealthChecks";

        public string Description => "Endpoints that takes care of node`s health";

        public string Author => "Nethermind";

        public bool MustInitialize => true;

        public FreeDiskSpaceChecker FreeDiskSpaceChecker => LazyInitializer.EnsureInitialized(ref _freeDiskSpaceChecker,
            () => new FreeDiskSpaceChecker(
                _healthChecksConfig,
                _api.FileSystem.GetDriveInfos(_initConfig.BaseDbPath),
                _api.TimerFactory,
                _api.ProcessExit,
                _logger));

        public Task Init(INethermindApi api)
        {
            _api = api;
            _healthChecksConfig = _api.Config<IHealthChecksConfig>();
            _jsonRpcConfig = _api.Config<IJsonRpcConfig>();
            _initConfig = _api.Config<IInitConfig>();
            _logger = api.LogManager.GetClassLogger();

            //will throw an exception and close app or block until enough disk space is available (LowStorageCheckAwaitOnStartup)
            EnsureEnoughFreeSpace();

            return Task.CompletedTask;
        }

        public void AddServices(IServiceCollection service)
        {
            service.AddHealthChecks()
                .AddTypeActivatedCheck<NodeHealthCheck>(
                    "node-health",
                    args: new object[] { _nodeHealthService, _api, _api.LogManager });
            if (_healthChecksConfig.UIEnabled)
            {
                if (!_healthChecksConfig.Enabled)
                {
                    if (_logger.IsWarn) _logger.Warn("To use HealthChecksUI please enable HealthChecks. (--HealthChecks.Enabled=true)");
                    return;
                }

                service.AddHealthChecksUI(setup =>
                {
                    setup.AddHealthCheckEndpoint("health", BuildEndpointForUi());
                    setup.SetEvaluationTimeInSeconds(_healthChecksConfig.PollingInterval);
                    setup.SetHeaderText("Nethermind Node Health");
                    if (_healthChecksConfig.WebhooksEnabled)
                    {
                        setup.AddWebhookNotification("webhook",
                            uri: _healthChecksConfig.WebhooksUri,
                            payload: _healthChecksConfig.WebhooksPayload,
                            restorePayload: _healthChecksConfig.WebhooksRestorePayload,
                            customDescriptionFunc: (livenessName, report) =>
                            {
                                string description = report.Entries["node-health"].Description;

                                IMetricsConfig metricsConfig = _api.Config<IMetricsConfig>();

                                string hostname = Dns.GetHostName();

                                HealthChecksWebhookInfo info = new(description, _api.IpResolver, metricsConfig, hostname);
                                return info.GetFullInfo();
                            }
                        );
                    }
                })
                .AddInMemoryStorage();
            }
        }
        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            IDriveInfo[] drives = Array.Empty<IDriveInfo>();

            if (_healthChecksConfig.LowStorageSpaceWarningThreshold > 0 || _healthChecksConfig.LowStorageSpaceShutdownThreshold > 0)
            {
                try
                {
                    drives = _api.FileSystem.GetDriveInfos(_initConfig.BaseDbPath);
                    FreeDiskSpaceChecker.StartAsync(default);
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error("Failed to initialize available disk space check module", ex);
                }
            }

            _nodeHealthService = new NodeHealthService(_api.SyncServer,
                _api.BlockchainProcessor!, _api.BlockProducer!, _healthChecksConfig, _api.HealthHintService!,
                _api.EthSyncingInfo!, _api.RpcCapabilitiesProvider, _api, drives, _initConfig.IsMining);

            if (_healthChecksConfig.Enabled)
            {
                HealthRpcModule healthRpcModule = new(_nodeHealthService);
                _api.RpcModuleProvider!.Register(new SingletonModulePool<IHealthRpcModule>(healthRpcModule, true));
                if (_logger.IsInfo) _logger.Info("Health RPC Module has been enabled");
            }

            if (_api.SpecProvider!.TerminalTotalDifficulty is not null)
            {
                _clHealthLogger = new ClHealthLogger(_nodeHealthService, _logger);
                _clHealthLogger.StartAsync(default);
            }

            return Task.CompletedTask;
        }

        private string BuildEndpointForUi()
        {
            string host = _jsonRpcConfig.Host.Replace("0.0.0.0", "localhost");
            host = host.Replace("[::]", "localhost");
            return new UriBuilder("http", host, _jsonRpcConfig.Port, _healthChecksConfig.Slug).ToString();
        }

        private void EnsureEnoughFreeSpace()
        {
            if (_healthChecksConfig.LowStorageSpaceShutdownThreshold > 0)
            {
                FreeDiskSpaceChecker.EnsureEnoughFreeSpaceOnStart(_api.TimerFactory);
            }
        }

        private class ClHealthLogger : IHostedService, IAsyncDisposable
        {
            private readonly INodeHealthService _nodeHealthService;
            private readonly ILogger _logger;

            private Timer _timer;

            public ClHealthLogger(INodeHealthService nodeHealthService, ILogger logger)
            {
                _nodeHealthService = nodeHealthService;
                _logger = logger;
            }

            public Task StartAsync(CancellationToken cancellationToken)
            {
                _timer = new Timer(ReportClStatus, null, TimeSpan.Zero,
                    TimeSpan.FromSeconds(ClUnavailableReportMessageDelay));

                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken)
            {
                _timer.Change(Timeout.Infinite, 0);

                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                await StopAsync(default);
                await _timer.DisposeAsync();
            }

            private void ReportClStatus(object _)
            {
                if (!_nodeHealthService.CheckClAlive())
                {
                    if (_logger.IsWarn)
                        _logger.Warn(
                            "No incoming messages from the Consensus Client. A Consensus Client is required to sync the node. Please make sure that it's working properly.");
                }
            }
        }
    }
}
