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

        private const int ClUnavailableReportMessageDelay = 5;

        public async ValueTask DisposeAsync()
        {
            if (_clHealthLogger is not null)
            {
                await _clHealthLogger.DisposeAsync();
            }
        }

        public string Name => "HealthChecks";

        public string Description => "Endpoints that takes care of node`s health";

        public string Author => "Nethermind";

        public Task Init(INethermindApi api)
        {
            _api = api;
            _healthChecksConfig = _api.Config<IHealthChecksConfig>();
            _jsonRpcConfig = _api.Config<IJsonRpcConfig>();
            _initConfig = _api.Config<IInitConfig>();

            _logger = api.LogManager.GetClassLogger();

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
                        customDescriptionFunc: report =>
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
            _nodeHealthService = new NodeHealthService(_api.SyncServer,
                _api.BlockchainProcessor!, _api.BlockProducer!, _healthChecksConfig, _api.HealthHintService!,
                _api.EthSyncingInfo!, _api, _initConfig.IsMining);

            if (_healthChecksConfig.Enabled)
            {
                HealthRpcModule healthRpcModule = new(_nodeHealthService);
                _api.RpcModuleProvider!.Register(new SingletonModulePool<IHealthRpcModule>(healthRpcModule, true));
                if (_logger.IsInfo) _logger.Info("Health RPC Module has been enabled");
            }

            if (_api.SpecProvider!.TerminalTotalDifficulty != null)
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
                            "No incoming messages from Consensus Client. Please make sure that it's working properly");
                }
            }
        }
    }
}
