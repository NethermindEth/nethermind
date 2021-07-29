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
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.JsonRpc;
using Nethermind.Monitoring.Metrics;
using Nethermind.Monitoring.Config;

namespace Nethermind.HealthChecks
{
    public class HealthChecksPlugin: INethermindPlugin, INethermindServicesPlugin
    {
        private INethermindApi _api;
        private IHealthChecksConfig _healthChecksConfig;
        private INodeHealthService _nodeHealthService;
        private ILogger _logger;
        private IJsonRpcConfig _jsonRpcConfig;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        public string Name => "HealthChecks";

        public string Description => "Endpoints that takes care of node`s health";

        public string Author => "Nethermind";

        public Task Init(INethermindApi api)
        {
            _api = api;
            _healthChecksConfig = _api.Config<IHealthChecksConfig>();
            _jsonRpcConfig = _api.Config<IJsonRpcConfig>();

            _logger = api.LogManager.GetClassLogger();
            
            return Task.CompletedTask;
        }

        public void AddServices(IServiceCollection service)
        {
            service.AddHealthChecks()
                .AddTypeActivatedCheck<NodeHealthCheck>(
                    "node-health", 
                    args: new object[] { _nodeHealthService });
            if (_healthChecksConfig.UIEnabled)
            {
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

                            IMetricsConfig metricsConfig;
                            metricsConfig = _api.Config<IMetricsConfig>();

                            string hostname = Dns.GetHostName();

                            HealthChecksWebhookInfo info = new HealthChecksWebhookInfo(description, _api.IpResolver, metricsConfig, hostname);
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
            if (_healthChecksConfig.Enabled)
            {
                IInitConfig initConfig = _api.Config<IInitConfig>();
                _nodeHealthService = new NodeHealthService(_api.SyncServer, new ReadOnlyBlockTree(_api.BlockTree), _api.BlockchainProcessor, _api.BlockProducer, _healthChecksConfig, _api.HealthHintService, initConfig.IsMining);
                HealthRpcModule healthRpcModule = new HealthRpcModule(_nodeHealthService);
                _api.RpcModuleProvider!.Register(new SingletonModulePool<IHealthRpcModule>(healthRpcModule, true));
                if (_logger.IsInfo) _logger.Info("Health RPC Module has been enabled");
            }

            return Task.CompletedTask;
        }

        private string BuildEndpointForUi()
        {
            string host = _jsonRpcConfig.Host.Replace("0.0.0.0", "localhost");
            host = host.Replace("[::]", "localhost");
            return new UriBuilder("http", host, _jsonRpcConfig.Port, _healthChecksConfig.Slug).ToString();
        }
    }
}
