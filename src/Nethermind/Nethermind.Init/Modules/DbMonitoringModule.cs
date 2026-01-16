// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;

namespace Nethermind.Init.Modules;

public class DbMonitoringModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        // Intercept created db to publish metric.
        // Dont use constructor injection to get all db because that would resolve all db
        // making them not lazy.
        builder
            .AddSingleton<DbTracker>()
            .AddDecorator<IDbFactory, DbTracker.DbFactoryInterceptor>()

            // Intercept block processing by checking the queue and pausing the metrics when that happen.
            // Dont use constructor injection because this would prevent the metric from being updated before
            // the block processing chain is constructed, eg: VerifyTrie or import jobs.
            .Intercept<IBlockProcessingQueue>((processingQueue, ctx) =>
            {
                if (!ctx.Resolve<IMetricsConfig>().PauseDbMetricDuringBlockProcessing) return;

                // Do not update db metrics while processing a block
                DbTracker updater = ctx.Resolve<DbTracker>();
                processingQueue.BlockAdded += (sender, args) => updater.Paused = !processingQueue.IsEmpty;
                processingQueue.BlockRemoved += (sender, args) => updater.Paused = !processingQueue.IsEmpty;
            })
            ;
    }

    public class DbTracker
    {
        private readonly ConcurrentDictionary<string, IDbMeta> _createdDbs = new ConcurrentDictionary<string, IDbMeta>();
        private readonly int _intervalSec;
        private readonly HyperClockCacheWrapper _sharedBlockCache;
        private long _lastDbMetricsUpdate = 0;

        private ILogger _logger;

        public DbTracker(IMonitoringService monitoringService, IMetricsConfig metricsConfig, HyperClockCacheWrapper sharedBlockCache, ILogManager logManager)
        {
            _intervalSec = metricsConfig.DbMetricIntervalSeconds;
            _logger = logManager.GetClassLogger<DbTracker>();
            _sharedBlockCache = sharedBlockCache;

            if (metricsConfig.EnableDbSizeMetrics)
            {
                monitoringService.AddMetricsUpdateAction(UpdateDbMetrics);
            }
        }

        public void AddDb(string name, IDbMeta dbMeta)
        {
            _createdDbs.TryAdd(name, dbMeta);
        }

        public IEnumerable<KeyValuePair<string, IDbMeta>> GetAllDbMeta()
        {
            return _createdDbs;
        }

        public bool Paused { get; set; } = false;

        private void UpdateDbMetrics()
        {
            try
            {
                if (Paused) return;

                if (Environment.TickCount64 - _lastDbMetricsUpdate < _intervalSec * 1000)
                {
                    // Update based on configured interval
                    return;
                }

                foreach (KeyValuePair<string, IDbMeta> kv in GetAllDbMeta())
                {
                    // Note: At the moment, the metric for a columns db is combined across column.
                    IDbMeta.DbMetric dbMetric = kv.Value.GatherMetric(); // Only include shared cache if state db
                    Db.Metrics.DbSize[kv.Key] = dbMetric.Size;
                    Db.Metrics.DbBlockCacheSize[kv.Key] = dbMetric.CacheSize;
                    Db.Metrics.DbMemtableSize[kv.Key] = dbMetric.MemtableSize;
                    Db.Metrics.DbIndexFilterSize[kv.Key] = dbMetric.IndexSize;
                    Db.Metrics.DbReads[kv.Key] = dbMetric.TotalReads;
                    Db.Metrics.DbWrites[kv.Key] = dbMetric.TotalWrites;
                }

                Db.Metrics.DbBlockCacheSize["Shared"] = _sharedBlockCache.GetUsage();

                _lastDbMetricsUpdate = Environment.TickCount64;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error during updating db metrics", e);
            }
        }

        public class DbFactoryInterceptor(DbTracker tracker, IDbFactory baseFactory) : IDbFactory
        {
            public IDb CreateDb(DbSettings dbSettings)
            {
                IDb db = baseFactory.CreateDb(dbSettings);
                if (db is IDbMeta dbMeta)
                {
                    tracker.AddDb(dbSettings.DbName, dbMeta);
                }
                return db;
            }

            public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
            {
                IColumnsDb<T> db = baseFactory.CreateColumnsDb<T>(dbSettings);
                if (db is IDbMeta dbMeta)
                {
                    tracker.AddDb(dbSettings.DbName, dbMeta);
                }
                return db;
            }

            public string GetFullDbPath(DbSettings dbSettings) => baseFactory.GetFullDbPath(dbSettings);
        }
    }
}
