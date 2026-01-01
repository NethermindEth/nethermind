// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using DotNetty.Buffers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;
using Nethermind.Monitoring.Metrics;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Init.Modules;

public class MonitoringModule(IMetricsConfig metricsConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (metricsConfig.Enabled || metricsConfig.CountersEnabled)
        {
            builder
                .AddSingleton<IMonitoringService, MonitoringService>()
                .AddSingleton<IMetricsController, IMetricsConfig, ISyncConfig, IPruningConfig>(
                    PrepareProductInfoMetrics)

                .Intercept<IMonitoringService>(ConfigureDefaultMetrics)

                .Intercept<IEthSyncingInfo>((syncInfo, ctx) =>
                {
                    ctx.Resolve<IMonitoringService>().AddMetricsUpdateAction(() =>
                    {
                        Synchronization.Metrics.SyncTime = (long?)syncInfo.UpdateAndGetSyncTime().TotalSeconds ?? 0;
                    });
                })

                ;
        }
        else
        {
            builder.AddSingleton<IMonitoringService, NoopMonitoringService>();
        }
    }

    private IMetricsController PrepareProductInfoMetrics(IMetricsConfig metricsConfig, ISyncConfig syncConfig, IPruningConfig pruningConfig)
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

        IMetricsController controller = new MetricsController(metricsConfig);

        IEnumerable<Type> metrics = TypeDiscovery.FindNethermindBasedTypes(nameof(Metrics));
        foreach (Type metric in metrics)
        {
            controller.RegisterMetrics(metric);
        }

        return controller;
    }

    private void ConfigureDefaultMetrics(IMonitoringService monitoringService, IComponentContext ctx)
    {
        // Note: Do not add dependencies outside of monitoring module.
        AllocatorMetricsUpdater allocatorMetricsUpdater = ctx.Resolve<AllocatorMetricsUpdater>();
        monitoringService.AddMetricsUpdateAction(() => allocatorMetricsUpdater.UpdateAllocatorMetrics());
    }

    private class AllocatorMetricsUpdater(ILogManager logManager)
    {
        ILogger _logger = logManager.GetClassLogger<AllocatorMetricsUpdater>();

        public void UpdateAllocatorMetrics()
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
    }
}
