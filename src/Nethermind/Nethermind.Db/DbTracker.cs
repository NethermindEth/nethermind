// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;
using NonBlocking;

namespace Nethermind.Db;

public class DbTracker
{
    private readonly ConcurrentDictionary<string, IDbMeta> _createdDbs = new ConcurrentDictionary<string, IDbMeta>();
    private readonly int _intervalSec;
    private long _lastDbMetricsUpdate = 0;

    private ILogger _logger;

    public DbTracker(IMonitoringService monitoringService, IMetricsConfig metricsConfig, ILogManager logManager)
    {
        _intervalSec = metricsConfig.DbMetricIntervalSeconds;
        _logger = logManager.GetClassLogger<DbTracker>();

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
                // Update max every minute
                return;
            }

            foreach (KeyValuePair<string, IDbMeta> kv in GetAllDbMeta())
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
