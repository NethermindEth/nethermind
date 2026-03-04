// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies()]
public class StartMonitoring(
    IMonitoringService monitoringService,
    ILogManager logManager,
    IMetricsConfig metricsConfig
    // ChainHeadInfoProvider chainHeadInfoProvider
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

        if (metricsConfig.Enabled)
        {
            await monitoringService.StartAsync().ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError)
                    _logger.Error("Error during starting a monitoring.", x.Exception);
            }, cancellationToken);
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
            monitoringService.AddMetricsUpdateAction(() => Task.Run(() => UpdateDbMetrics()));
        }

        if (metricsConfig.EnableDetailedMetric)
        {
            monitoringService.AddMetricsUpdateAction(() => Task.Run(() => UpdateAllocatorMetrics()));
        }

        monitoringService.AddMetricsUpdateAction(() =>
        {
            Synchronization.Metrics.SyncTime = (long?)ethSyncingInfo?.UpdateAndGetSyncTime().TotalSeconds ?? 0;
        });
    }

    private bool _isUpdatingDbMetrics = false;
    private long _lastDbMetricsUpdate = 0;
    private void UpdateDbMetrics()
    {
        if (!Interlocked.Exchange(ref _isUpdatingDbMetrics, true))
        {
            try
            {
                if (Environment.TickCount64 - _lastDbMetricsUpdate < 5_000)
                {
                    // Update max every minute
                    return;
                }
                /*
                if (chainHeadInfoProvider.IsProcessingBlock)
                {
                    // Do not update db metrics while processing a block
                    return;
                }
                */

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
                _lastDbMetricsUpdate = Environment.TickCount64;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error during updating db metrics", e);
            }
            finally
            {
                Volatile.Write(ref _isUpdatingDbMetrics, false);
            }
        }
    }

    private bool _isUpdatingAllocatorMetrics = false;
    private void UpdateAllocatorMetrics()
    {
        if (!Interlocked.Exchange(ref _isUpdatingAllocatorMetrics, true))
        {
            try
            {
                SetAllocatorMetrics(NethermindBuffers.RlpxAllocator, "rlpx");
                SetAllocatorMetrics(NethermindBuffers.DiscoveryAllocator, "discovery");
                SetAllocatorMetrics(NethermindBuffers.Default, "default");
                SetAllocatorMetrics(PooledByteBufferAllocator.Default, "netty_default");
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error during allocator metrics", e);
            }
            finally
            {
                Volatile.Write(ref _isUpdatingAllocatorMetrics, false);
            }
        }
    }

    private static void SetAllocatorMetrics(IByteBufferAllocator allocator, string name)
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
