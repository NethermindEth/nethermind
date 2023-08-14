// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
    private readonly IMetricsConfig _metricsConfig;

    public StartMonitoring(INethermindApi api)
    {
        _api = api;
        _logger = _api.LogManager.GetClassLogger();
        _metricsConfig = _api.Config<IMetricsConfig>();
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        // hacky
        if (!string.IsNullOrEmpty(_metricsConfig.NodeName))
        {
            _api.LogManager.SetGlobalVariable("nodeName", _metricsConfig.NodeName);
        }

        MetricsController? controller = null;
        if (_metricsConfig.Enabled || _metricsConfig.CountersEnabled)
        {
            PrepareProductInfoMetrics();
            controller = new(_metricsConfig);

            IEnumerable<Type> metrics = TypeDiscovery.FindNethermindBasedTypes(nameof(Metrics));
            foreach (Type metric in metrics)
            {
                controller.RegisterMetrics(metric);
            }
        }

        if (_metricsConfig.Enabled)
        {
            IMonitoringService monitoringService = _api.MonitoringService = new MonitoringService(controller, _metricsConfig, _api.LogManager);

            SetupMetrics(monitoringService);

            await monitoringService.StartAsync().ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError)
                    _logger.Error("Error during starting a monitoring.", x.Exception);
            }, cancellationToken);

            _api.DisposeStack.Push(new Reactive.AnonymousDisposable(() => monitoringService.StopAsync())); // do not await
        }
        else
        {
            if (_logger.IsInfo)
                _logger.Info("Grafana / Prometheus metrics are disabled in configuration");
        }

        if (_logger.IsInfo)
        {
            _logger.Info(_metricsConfig.CountersEnabled
                ? "System.Diagnostics.Metrics enabled and will be collectable with dotnet-counters"
                : "System.Diagnostics.Metrics disabled");
        }
    }

    private void SetupMetrics(IMonitoringService monitoringService)
    {
        if (_metricsConfig.EnableDbSizeMetrics)
        {
            monitoringService.AddMetricsUpdateAction(() =>
            {
                IDbProvider? dbProvider = _api.DbProvider;
                if (dbProvider is null)
                {
                    return;
                }

                Db.Metrics.StateDbSize = dbProvider.StateDb.GetSize();
                Db.Metrics.ReceiptsDbSize = dbProvider.ReceiptsDb.GetSize();
                Db.Metrics.HeadersDbSize = dbProvider.HeadersDb.GetSize();
                Db.Metrics.BlocksDbSize = dbProvider.BlocksDb.GetSize();
                Db.Metrics.BloomDbSize = dbProvider.BloomDb.GetSize();
                Db.Metrics.CodeDbSize = dbProvider.CodeDb.GetSize();
                Db.Metrics.BlockInfosDbSize = dbProvider.BlockInfosDb.GetSize();
                Db.Metrics.ChtDbSize = dbProvider.ChtDb.GetSize();
                Db.Metrics.MetadataDbSize = dbProvider.MetadataDb.GetSize();
                Db.Metrics.WitnessDbSize = dbProvider.WitnessDb.GetSize();

                Db.Metrics.DbBlockCacheMemorySize = dbProvider.StateDb.GetCacheSize()
                                                    + dbProvider.BlockInfosDb.GetCacheSize()
                                                    + dbProvider.HeadersDb.GetCacheSize()
                                                    + dbProvider.BlocksDb.GetCacheSize()
                                                    + dbProvider.ReceiptsDb.GetCacheSize();
                // Share same cache with StateDb
                // + dbProvider.ChtDb.GetCacheSize()
                // + dbProvider.MetadataDb.GetCacheSize()
                // + dbProvider.WitnessDb.GetCacheSize()
                // + dbProvider.CodeDb.GetCacheSize()
                // + dbProvider.BloomDb.GetCacheSize()

                Db.Metrics.DbIndexFilterMemorySize = dbProvider.StateDb.GetIndexSize()
                                                    + dbProvider.ReceiptsDb.GetIndexSize()
                                                    + dbProvider.HeadersDb.GetIndexSize()
                                                    + dbProvider.BlocksDb.GetIndexSize()
                                                    + dbProvider.BloomDb.GetIndexSize()
                                                    + dbProvider.CodeDb.GetIndexSize()
                                                    + dbProvider.BlockInfosDb.GetIndexSize()
                                                    + dbProvider.ChtDb.GetIndexSize()
                                                    + dbProvider.MetadataDb.GetIndexSize()
                                                    + dbProvider.WitnessDb.GetIndexSize();

                Db.Metrics.DbMemtableMemorySize = dbProvider.StateDb.GetMemtableSize()
                                                    + dbProvider.ReceiptsDb.GetMemtableSize()
                                                    + dbProvider.HeadersDb.GetMemtableSize()
                                                    + dbProvider.BlocksDb.GetMemtableSize()
                                                    + dbProvider.BloomDb.GetMemtableSize()
                                                    + dbProvider.CodeDb.GetMemtableSize()
                                                    + dbProvider.BlockInfosDb.GetMemtableSize()
                                                    + dbProvider.ChtDb.GetMemtableSize()
                                                    + dbProvider.MetadataDb.GetMemtableSize()
                                                    + dbProvider.WitnessDb.GetMemtableSize();

                Db.Metrics.DbTotalMemorySize = Db.Metrics.DbBlockCacheMemorySize
                                                + Db.Metrics.DbIndexFilterMemorySize
                                                + Db.Metrics.DbMemtableMemorySize;
            });
        }

        monitoringService.AddMetricsUpdateAction(() =>
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
