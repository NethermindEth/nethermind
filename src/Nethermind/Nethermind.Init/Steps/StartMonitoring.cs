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
// 

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;
using Nethermind.Monitoring.Metrics;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork))]
    public class StartMonitoring : IStep
    {
        private readonly IApiWithNetwork _api;

        public StartMonitoring(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            IMetricsConfig metricsConfig = _api.Config<IMetricsConfig>();
            ILogger logger = _api.LogManager.GetClassLogger();
            
            // hacky
            if (!string.IsNullOrEmpty(metricsConfig.NodeName))
            {
                _api.LogManager.SetGlobalVariable("nodeName", metricsConfig.NodeName);
            }
            
            if (metricsConfig.Enabled)
            {
                Metrics.Version = VersionToMetrics.ConvertToNumber(ClientVersion.Version);
                MetricsUpdater metricsUpdater = new MetricsUpdater(metricsConfig);
                _api.MonitoringService = new MonitoringService(metricsUpdater, metricsConfig, _api.LogManager);
                var metrics = new TypeDiscovery().FindNethermindTypes(nameof(Metrics));
                foreach (var metric in metrics)
                {
                    _api.MonitoringService.RegisterMetrics(metric);    
                }

                await _api.MonitoringService.StartAsync().ContinueWith(x =>
                {
                    if (x.IsFaulted && logger.IsError) 
                        logger.Error("Error during starting a monitoring.", x.Exception);
                }, cancellationToken);
                
                _api.DisposeStack.Push(new Reactive.AnonymousDisposable(() => _api.MonitoringService.StopAsync())); // do not await
            }
            else
            {
                if (logger.IsInfo) 
                    logger.Info("Grafana / Prometheus metrics are disabled in configuration");
            }
        }
    }
}
