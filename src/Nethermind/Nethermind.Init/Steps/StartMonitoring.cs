// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        MetricsController? controller = null;
        if (metricsConfig.Enabled || metricsConfig.CountersEnabled)
        {
            PrepareProductInfoMetrics();
            controller = new(metricsConfig);

            IEnumerable<Type> metrics = TypeDiscovery.FindNethermindTypes(nameof(Metrics));
            foreach (Type metric in metrics)
            {
                controller.RegisterMetrics(metric);
            }
        }

        if (metricsConfig.Enabled)
        {
            _api.MonitoringService = new MonitoringService(controller, metricsConfig, _api.LogManager);

            await _api.MonitoringService.StartAsync().ContinueWith(x =>
            {
                if (x.IsFaulted && logger.IsError)
                    logger.Error("Error during starting a monitoring.", x.Exception);
            }, cancellationToken);

            AddMetricsUpdateActions();

            _api.DisposeStack.Push(new Reactive.AnonymousDisposable(() => _api.MonitoringService.StopAsync())); // do not await
        }
        else
        {
            if (logger.IsInfo)
                logger.Info("Grafana / Prometheus metrics are disabled in configuration");
        }

        if (logger.IsInfo)
        {
            logger.Info(metricsConfig.CountersEnabled
                ? "System.Diagnostics.Metrics enabled and will be collectable with dotnet-counters"
                : "System.Diagnostics.Metrics disabled");
        }
    }

    private void AddMetricsUpdateActions()
    {
        _api.MonitoringService.AddMetricsUpdateAction(() =>
        {
            Db.Metrics.StateDbSize = _api.DbProvider!.StateDb.GetSize();
            Db.Metrics.ReceiptsDbSize = _api.DbProvider!.ReceiptsDb.GetSize();
            Db.Metrics.HeadersDbSize = _api.DbProvider!.HeadersDb.GetSize();
            Db.Metrics.BlocksDbSize = _api.DbProvider!.BlocksDb.GetSize();

            Db.Metrics.DbSize = _api.DbProvider!.RegisteredDbs.Values.Aggregate(0L, (sum, db) => sum + db.GetSize());
        });
    }

    private static void PrepareProductInfoMetrics()
    {
        Metrics.Version = VersionToMetrics.ConvertToNumber(ProductInfo.Version);
    }

    public bool MustInitialize => false;
}
