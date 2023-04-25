// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
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
    private readonly ILogger _logger;

    public StartMonitoring(INethermindApi api)
    {
        _api = api;
        _logger = _api.LogManager.GetClassLogger();
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        IMetricsConfig metricsConfig = _api.Config<IMetricsConfig>();

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
                if (x.IsFaulted && _logger.IsError)
                    _logger.Error("Error during starting a monitoring.", x.Exception);
            }, cancellationToken);

            SetupMetrics();

            _api.DisposeStack.Push(new Reactive.AnonymousDisposable(() => _api.MonitoringService.StopAsync())); // do not await
        }
        else
        {
            if (_logger.IsInfo)
                _logger.Info("Grafana / Prometheus metrics are disabled in configuration");
        }

        if (_logger.IsInfo)
        {
            _logger.Info(metricsConfig.CountersEnabled
                ? "System.Diagnostics.Metrics enabled and will be collectable with dotnet-counters"
                : "System.Diagnostics.Metrics disabled");
        }
    }

    private void SetupMetrics()
    {
        _api.MonitoringService.AddMetricsUpdateAction(() =>
        {
            try
            {
                Db.Metrics.StateDbSize = _api.DbProvider!.StateDb.GetSize();
                Db.Metrics.ReceiptsDbSize = _api.DbProvider!.ReceiptsDb.GetSize();
                Db.Metrics.HeadersDbSize = _api.DbProvider!.HeadersDb.GetSize();
                Db.Metrics.BlocksDbSize = _api.DbProvider!.BlocksDb.GetSize();
                Db.Metrics.BloomDbSize = _api.DbProvider!.BloomDb.GetSize();
                Db.Metrics.CodeDbSize = _api.DbProvider!.CodeDb.GetSize();
                Db.Metrics.BlockInfosDbSize = _api.DbProvider!.BlockInfosDb.GetSize();
                Db.Metrics.ChtDbSize = _api.DbProvider!.ChtDb.GetSize();
                Db.Metrics.MetadataDbSize = _api.DbProvider!.MetadataDb.GetSize();
                Db.Metrics.WitnessDbSize = _api.DbProvider!.WitnessDb.GetSize();
            }
            catch (Exception e)
            {
                if (_logger.IsWarn)
                    _logger.Error($"Failed to update DB size metrics {e.Message}");
            }
        });

        _api.MonitoringService.AddMetricsUpdateAction(() =>
        {
            Synchronization.Metrics.SyncTime = (long?)_api.EthSyncingInfo?.UpdateAndGetSyncTime().TotalSeconds ?? 0;
        });
    }

    private void PrepareProductInfoMetrics()
    {
        IPruningConfig pruningConfig = _api.Config<IPruningConfig>();
        IMetricsConfig metricsConfig = _api.Config<IMetricsConfig>();
        ISyncConfig syncConfig = _api.Config<ISyncConfig>();
        ProductInfo.Instance = metricsConfig.NodeName;

        if (syncConfig.SnapSync)
        {
            ProductInfo.SyncType = "Snap";
        }
        else if (syncConfig.FastSync)
        {
            ProductInfo.SyncType = "Fast";
        }
        else
        {
            ProductInfo.SyncType = "Full";
        }

        ProductInfo.PruningMode = pruningConfig.Mode.ToString();
        Metrics.Version = VersionToMetrics.ConvertToNumber(ProductInfo.Version);
    }

    public bool MustInitialize => false;
}
