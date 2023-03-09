// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;
using Nethermind.Monitoring.Metrics;
using Type = System.Type;

namespace Nethermind.Init.Steps;

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
            PrepareProductInfoMetrics();
            MetricsController metricsController = new(metricsConfig);

            _api.MonitoringService = new MonitoringService(metricsController, metricsConfig, _api.LogManager);
            IEnumerable<Type> metrics = TypeDiscovery.FindNethermindTypes(nameof(Metrics));
            foreach (Type metric in metrics)
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

    private static void PrepareProductInfoMetrics()
    {
        Metrics.Version = VersionToMetrics.ConvertToNumber(ProductInfo.Version);
    }

    public bool MustInitialize => false;
}
