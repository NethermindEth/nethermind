// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
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

    public class DbTracker : IDisposable
    {
        private const int PausedUpdateHoldoffMultiplier = 10;

        private readonly ConcurrentDictionary<string, IDbMeta> _createdDbs = new();
        private readonly HashSet<string> _failingDbs = [];
        private readonly Lock _publishLock = new();
        private readonly int _intervalSec;
        private readonly bool _publishOnCreation;
        private readonly Lazy<HyperClockCacheWrapper> _sharedBlockCache;
        private long _lastDbMetricsUpdate = 0;
        private volatile bool _stopped;

        private ILogger _logger;

        public DbTracker(IMonitoringService monitoringService, IMetricsConfig metricsConfig, Lazy<HyperClockCacheWrapper> sharedBlockCache, ILogManager logManager)
        {
            _intervalSec = metricsConfig.DbMetricIntervalSeconds;
            _logger = logManager.GetClassLogger<DbTracker>();
            _sharedBlockCache = sharedBlockCache;
            _publishOnCreation = metricsConfig.Enabled && metricsConfig.EnableDbSizeMetrics;

            if (metricsConfig.EnableDbSizeMetrics)
            {
                monitoringService.AddMetricsUpdateAction(UpdateDbMetrics);
            }
        }

        public void AddDb(string name, IDbMeta dbMeta)
        {
            if (_createdDbs.TryAdd(name, dbMeta) && _publishOnCreation)
            {
                PublishDbMetrics(name, dbMeta);
            }
        }

        internal long LastDbMetricsUpdate
        {
            get => _lastDbMetricsUpdate;
            set => _lastDbMetricsUpdate = value;
        }

        public IEnumerable<KeyValuePair<string, IDbMeta>> GetAllDbMeta() => _createdDbs;

        public bool Paused { get; set; } = false;

        /// <summary>
        /// Disposed by Autofac when the owning lifetime scope is torn down. Setting
        /// <c>_stopped</c> here short-circuits any subsequent monitoring tick before it
        /// touches disposed resources (<c>_sharedBlockCache.Value</c>, etc.). The
        /// <c>catch (ObjectDisposedException)</c> in <see cref="UpdateDbMetrics"/> remains
        /// as a backstop for the race where a tick is already executing when Dispose runs.
        /// </summary>
        public void Dispose() => _stopped = true;

        private void UpdateDbMetrics()
        {
            if (_stopped) return;
            try
            {
                if (_lastDbMetricsUpdate != 0)
                {
                    long sinceLastUpdate = Environment.TickCount64 - _lastDbMetricsUpdate;

                    if (sinceLastUpdate < _intervalSec * 1000L)
                    {
                        // Update based on configured interval
                        return;
                    }

                    if (Paused && sinceLastUpdate < _intervalSec * 1000L * PausedUpdateHoldoffMultiplier) return;
                }

                foreach (KeyValuePair<string, IDbMeta> kv in GetAllDbMeta())
                {
                    PublishDbMetrics(kv.Key, kv.Value);
                }

                lock (_publishLock)
                {
                    Db.Metrics.DbBlockCacheSize["Shared"] = _sharedBlockCache.Value.GetUsage();
                }

                _lastDbMetricsUpdate = Environment.TickCount64;
            }
            catch (ObjectDisposedException)
            {
                if (_logger.IsDebug) _logger.Debug("DbTracker stopping metrics updates: DI scope or shared cache has been disposed.");
                _stopped = true;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error during updating db metrics", e);
            }
        }

        private void PublishDbMetrics(string name, IDbMeta dbMeta)
        {
            lock (_publishLock)
            {
                try
                {
                    // Note: At the moment, the metric for a columns db is combined across column.
                    IDbMeta.DbMetric dbMetric = dbMeta.GatherMetric();
                    Db.Metrics.DbSize[name] = dbMetric.Size;
                    Db.Metrics.DbBlockCacheSize[name] = dbMetric.CacheSize;
                    Db.Metrics.DbMemtableSize[name] = dbMetric.MemtableSize;
                    Db.Metrics.DbIndexFilterSize[name] = dbMetric.IndexSize;
                    Db.Metrics.DbReads[name] = dbMetric.TotalReads;
                    Db.Metrics.DbWrites[name] = dbMetric.TotalWrites;
                    if (_failingDbs.Remove(name) && _logger.IsInfo)
                        _logger.Info($"DB metric collection recovered for '{name}'");
                }
                catch (Exception e)
                {
                    // Remove stale entries so Prometheus does not report old values indefinitely.
                    RemoveStaleMetricEntry(name);
                    // Log only on the first failure of a streak; recovery is logged when GatherMetric succeeds again.
                    if (_failingDbs.Add(name) && _logger.IsWarn)
                        _logger.Warn($"Failed to gather metrics for DB '{name}': {e.Message}");
                }
            }
        }

        // Cast to IDictionary<string, long> so this works for both the default NonBlocking.ConcurrentDictionary
        // and the plain Dictionary used under the ZK_EVM compile flag.
        private static readonly IDictionary<string, long>[] _perDbMetricMaps =
        {
            Db.Metrics.DbReads, Db.Metrics.DbWrites, Db.Metrics.DbSize,
            Db.Metrics.DbMemtableSize, Db.Metrics.DbBlockCacheSize, Db.Metrics.DbIndexFilterSize,
        };

        private static void RemoveStaleMetricEntry(string name)
        {
            foreach (IDictionary<string, long> map in _perDbMetricMaps)
                map.Remove(name);
        }

        public class DbFactoryInterceptor(DbTracker tracker, IDbFactory baseFactory) : IDbFactory
        {
            public IDb CreateDb(DbSettings dbSettings)
            {
                IDb db = baseFactory.CreateDb(dbSettings);
                if (!dbSettings.SkipMetricsTracking && db is IDbMeta dbMeta)
                {
                    tracker.AddDb(dbSettings.DbName, dbMeta);
                }
                return db;
            }

            public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
            {
                IColumnsDb<T> db = baseFactory.CreateColumnsDb<T>(dbSettings);
                if (!dbSettings.SkipMetricsTracking && db is IDbMeta dbMeta)
                {
                    tracker.AddDb(dbSettings.DbName, dbMeta);
                }
                return db;
            }

            public string GetFullDbPath(DbSettings dbSettings) => baseFactory.GetFullDbPath(dbSettings);
        }
    }
}
