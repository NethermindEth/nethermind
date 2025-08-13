// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.ServiceStopper;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;
using Nethermind.Monitoring.Metrics;
using Nethermind.Serialization.Rlp;
using Type = System.Type;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeBlockchain))]
public class StartMonitoring(
    IEthSyncingInfo ethSyncingInfo,
    DbTracker dbTracker,
    IPruningConfig pruningConfig,
    ISyncConfig syncConfig,
    IServiceStopper serviceStopper,
    ILogManager logManager,
    IMetricsConfig metricsConfig
) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public async Task Execute(CancellationToken cancellationToken)
    {
        // hacky
        if (!string.IsNullOrEmpty(metricsConfig.NodeName))
        {
            logManager.SetGlobalVariable("nodeName", metricsConfig.NodeName);
        }

        MetricsController? controller = null;
        if (metricsConfig.Enabled || metricsConfig.CountersEnabled)
        {
            PrepareProductInfoMetrics();
            controller = new(metricsConfig);

            IEnumerable<Type> metrics = TypeDiscovery.FindNethermindBasedTypes(nameof(Metrics));
            foreach (Type metric in metrics)
            {
                controller.RegisterMetrics(metric);
            }
        }

        if (metricsConfig.Enabled)
        {
            MonitoringService monitoringService = new(controller, metricsConfig, logManager);

            SetupMetrics(monitoringService);

            await monitoringService.StartAsync().ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError)
                    _logger.Error("Error during starting a monitoring.", x.Exception);
            }, cancellationToken);

            serviceStopper.AddStoppable(monitoringService);
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

    private void SetupMetrics(MonitoringService monitoringService)
    {
        if (metricsConfig.EnableDbSizeMetrics)
        {
            monitoringService.AddMetricsUpdateAction(() =>
            {
                foreach (KeyValuePair<string, IDbMeta> kv in dbTracker.GetAllDbMeta())
                {
                    // Note: At the moment, the metric for a columns db is combined across column.
                    IDbMeta.DbMetric dbMetric = kv.Value.GatherMetric(includeSharedCache: kv.Key == DbNames.State); // Only include shared cache if state db
                    Db.Metrics.DbSize[kv.Key] = dbMetric.Size;
                    Db.Metrics.DbBlockCacheSize[kv.Key] = dbMetric.CacheSize;
                    Db.Metrics.DbMemtableSize[kv.Key] = dbMetric.MemtableSize;
                    Db.Metrics.DbIndexFilterSize[kv.Key] = dbMetric.IndexSize;
                    Db.Metrics.DbReads[kv.Key] = dbMetric.TotalReads;
                    Db.Metrics.DbWrites[kv.Key] = dbMetric.TotalWrites;
                }
            });
        }

        if (metricsConfig.EnableDetailedMetric)
        {
            monitoringService.AddMetricsUpdateAction(() =>
            {
                SetAllocatorMetrics(NethermindBuffers.RlpxAllocator, "rlpx");
                SetAllocatorMetrics(NethermindBuffers.DiscoveryAllocator, "discovery");
                SetAllocatorMetrics(NethermindBuffers.Default, "default");
                SetAllocatorMetrics(PooledByteBufferAllocator.Default, "netty_default");
            });
        }

        monitoringService.AddMetricsUpdateAction(() =>
        {
            Synchronization.Metrics.SyncTime = (long?)ethSyncingInfo?.UpdateAndGetSyncTime().TotalSeconds ?? 0;
        });
    }

    public static void SetAllocatorMetrics(IByteBufferAllocator allocator, string name)
    {
        if (allocator is PooledByteBufferAllocator byteBufferAllocator)
        {
            PooledByteBufferAllocatorMetric metric = byteBufferAllocator.Metric;
            Serialization.Rlp.Metrics.AllocatorArenaCount[name] = metric.DirectArenas().Count;
            Serialization.Rlp.Metrics.AllocatorChunkSize[name] = metric.ChunkSize;
            Serialization.Rlp.Metrics.AllocatorUsedHeapMemory[name] = metric.UsedHeapMemory;
            Serialization.Rlp.Metrics.AllocatorUsedDirectMemory[name] = metric.UsedDirectMemory;
            Serialization.Rlp.Metrics.AllocatorActiveAllocations[name] = metric.HeapArenas().Sum((it) => it.NumActiveAllocations);
            Serialization.Rlp.Metrics.AllocatorActiveAllocationBytes[name] = metric.HeapArenas().Sum((it) => it.NumActiveBytes);
            Serialization.Rlp.Metrics.AllocatorAllocations[name] = metric.HeapArenas().Sum((it) => it.NumAllocations);
        }
    }

    private void PrepareProductInfoMetrics()
    {
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
