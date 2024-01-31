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

                long totalBlockCacheMemorySize = 0;
                long totalIndexAndFilterMemorySize = 0;
                long totalMemtableMemorySize = 0;

                foreach (KeyValuePair<string, IDbMeta> kv in dbProvider.GetAllDbMeta())
                {
                    // Note: At the moment, the metric for a columns db is combined across column.
                    IDbMeta.DbMetric dbMetric = kv.Value.GatherMetric(includeSharedCache: kv.Key == DbNames.State); // Only include shared cache if state db
                    Db.Metrics.DbSize[kv.Key] = dbMetric.Size;
                    Db.Metrics.BlockCacheSize[kv.Key] = dbMetric.CacheSize;
                    Db.Metrics.MemtableSize[kv.Key] = dbMetric.MemtableSize;
                    Db.Metrics.IndexFilterSize[kv.Key] = dbMetric.IndexSize;
                    Db.Metrics.DbReads[kv.Key] = dbMetric.TotalReads;
                    Db.Metrics.DbWrites[kv.Key] = dbMetric.TotalWrites;

                    totalBlockCacheMemorySize += dbMetric.CacheSize;
                    totalIndexAndFilterMemorySize += dbMetric.IndexSize;
                    totalMemtableMemorySize += dbMetric.MemtableSize;
                }

                // You dont need this, just use `sum` in prometheus, this is what happen when you don't use label.
                Db.Metrics.DbBlockCacheMemorySize = totalBlockCacheMemorySize;
                Db.Metrics.DbIndexFilterMemorySize = totalIndexAndFilterMemorySize;
                Db.Metrics.DbMemtableMemorySize = totalMemtableMemorySize;
                Db.Metrics.DbTotalMemorySize = Db.Metrics.DbBlockCacheMemorySize
                                                + Db.Metrics.DbIndexFilterMemorySize
                                                + Db.Metrics.DbMemtableMemorySize;


                // Please don't use these anymore. Just use label... I'm just adding it here to not break existing dashboard.
                // TODO: Remove this... please
                IDbMeta.DbMetric stateDbMetric = dbProvider.StateDb.GatherMetric(includeSharedCache: true);
                IDbMeta.DbMetric receiptDbMetric = dbProvider.ReceiptsDb.GatherMetric();
                IDbMeta.DbMetric headerDbMetric = dbProvider.HeadersDb.GatherMetric();
                IDbMeta.DbMetric blocksDbMetric = dbProvider.BlocksDb.GatherMetric();
                IDbMeta.DbMetric blockNumberDbMetric = dbProvider.BlockNumbersDb.GatherMetric();
                IDbMeta.DbMetric badBlockDbMetric = dbProvider.BadBlocksDb.GatherMetric();
                IDbMeta.DbMetric bloomDbMetric = dbProvider.BloomDb.GatherMetric();
                IDbMeta.DbMetric codeDbMetric = dbProvider.CodeDb.GatherMetric();
                IDbMeta.DbMetric blockInfoDbMetric = dbProvider.BlockInfosDb.GatherMetric();
                IDbMeta.DbMetric chtDbMetric = dbProvider.ChtDb.GatherMetric();
                IDbMeta.DbMetric metadataDbMetric = dbProvider.MetadataDb.GatherMetric();
                IDbMeta.DbMetric witnessDbMetric = dbProvider.WitnessDb.GatherMetric();
                IDbMeta.DbMetric blobTransactionDbMetric = dbProvider.BlobTransactionsDb.GatherMetric();

                Db.Metrics.StateDbSize = stateDbMetric.Size;
                Db.Metrics.StateDbReads = stateDbMetric.TotalReads;
                Db.Metrics.StateDbWrites = stateDbMetric.TotalWrites;

                Db.Metrics.ReceiptsDbSize = receiptDbMetric.Size;
                Db.Metrics.ReceiptsDbReads = receiptDbMetric.TotalReads;
                Db.Metrics.ReceiptsDbWrites = receiptDbMetric.TotalWrites;

                // Look at what just happen, one metric have `s`, two other dont. Just use label.
                Db.Metrics.HeadersDbSize = headerDbMetric.Size;
                Db.Metrics.HeaderDbReads = headerDbMetric.TotalReads;
                Db.Metrics.HeaderDbWrites = headerDbMetric.TotalWrites;

                Db.Metrics.BlocksDbSize = blocksDbMetric.Size;
                Db.Metrics.BlocksDbReads = blocksDbMetric.TotalReads;
                Db.Metrics.BlocksDbWrites = blocksDbMetric.TotalWrites;

                // Guess what? this one does not have db size and stuff.
                Db.Metrics.BlockNumberDbReads = blockNumberDbMetric.TotalReads;
                Db.Metrics.BlockNumberDbReads = blockNumberDbMetric.TotalWrites;

                Db.Metrics.BadBlocksDbReads = badBlockDbMetric.TotalReads;
                Db.Metrics.BadBlocksDbWrites = badBlockDbMetric.TotalWrites;

                Db.Metrics.BloomDbSize = bloomDbMetric.Size;
                Db.Metrics.BloomDbReads = bloomDbMetric.TotalReads;
                Db.Metrics.BloomDbWrites = bloomDbMetric.TotalWrites;

                Db.Metrics.CodeDbSize = codeDbMetric.Size;
                Db.Metrics.CodeDbReads = codeDbMetric.TotalReads;
                Db.Metrics.CodeDbWrites = codeDbMetric.TotalWrites;

                Db.Metrics.BlockInfosDbSize = blockInfoDbMetric.Size;
                Db.Metrics.BlockInfosDbReads = blockInfoDbMetric.TotalReads;
                Db.Metrics.BlockInfosDbWrites = blockInfoDbMetric.TotalWrites;

                // Ooo look! One is capitalized differently. **snap a picture like in a zoo**
                Db.Metrics.ChtDbSize = chtDbMetric.Size;
                Db.Metrics.CHTDbReads = chtDbMetric.TotalReads;
                Db.Metrics.CHTDbWrites = chtDbMetric.TotalWrites;

                Db.Metrics.MetadataDbSize = metadataDbMetric.Size;
                Db.Metrics.MetadataDbReads = metadataDbMetric.TotalReads;
                Db.Metrics.MetadataDbWrites = metadataDbMetric.TotalWrites;

                Db.Metrics.WitnessDbSize = witnessDbMetric.Size;
                Db.Metrics.WitnessDbReads = witnessDbMetric.TotalReads;
                Db.Metrics.WitnessDbReads = witnessDbMetric.TotalWrites;

                Db.Metrics.BlobTransactionsDbReads = blobTransactionDbMetric.TotalReads;
                Db.Metrics.BlobTransactionsDbWrites = blobTransactionDbMetric.TotalWrites;
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
