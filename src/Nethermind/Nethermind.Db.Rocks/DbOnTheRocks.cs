//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Nethermind.Core;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rocks.Statistics;
using Nethermind.Logging;
using RocksDbSharp;

namespace Nethermind.Db.Rocks
{
    public class DbOnTheRocks : IDbWithSpan
    {
        private ILogger _logger;
        
        private string? _fullPath;
        
        private static readonly ConcurrentDictionary<string, RocksDb> _dbsByPath = new();

        private bool _isDisposed;

        private readonly HashSet<IBatch> _currentBatches = new();
        
        internal readonly RocksDb _db;
        internal WriteOptions? WriteOptions { get; private set; }

        internal DbOptions? DbOptions { get; private set; }

        public string Name { get; }

        private static long _maxRocksSize;

        private long _maxThisDbSize;

        private static int _cacheInitialized;

        protected static IntPtr _cache;

        private readonly RocksDbSettings _settings;

        protected static void InitCache(IDbConfig dbConfig)
        {
            if (Interlocked.CompareExchange(ref _cacheInitialized, 1, 0) == 0)
            {
                _cache = RocksDbSharp.Native.Instance.rocksdb_cache_create_lru(new UIntPtr(dbConfig.BlockCacheSize));
                Interlocked.Add(ref _maxRocksSize, (long)dbConfig.BlockCacheSize);
            }
        }

        public DbOnTheRocks(string basePath, RocksDbSettings rocksDbSettings, IDbConfig dbConfig,
            ILogManager logManager, ColumnFamilies? columnFamilies = null, bool deleteOnStart = false)
        {
            _logger = logManager.GetClassLogger();
            _settings = rocksDbSettings;
            Name = _settings.DbName;
            _db = Init(basePath, rocksDbSettings.DbPath, dbConfig, logManager, columnFamilies, deleteOnStart);
        }

        private RocksDb Init(string basePath, string dbPath, IDbConfig dbConfig, ILogManager? logManager,
            ColumnFamilies? columnFamilies = null, bool deleteOnStart = false)
        {
            static RocksDb Open(string path, (DbOptions Options, ColumnFamilies? Families) db)
            {
                (DbOptions options, ColumnFamilies? families) = db;
                return families == null ? RocksDb.Open(options, path) : RocksDb.Open(options, path, families);
            }

            _fullPath = dbPath.GetApplicationResourcePath(basePath);
            _logger = logManager?.GetClassLogger() ?? NullLogger.Instance;
            if (!Directory.Exists(_fullPath))
            {
                Directory.CreateDirectory(_fullPath);
            }
            else if (deleteOnStart)
            {
                Clear();
            }

            try
            {
                // ReSharper disable once VirtualMemberCallInConstructor
                if (_logger.IsDebug) _logger.Debug($"Building options for {Name} DB");
                DbOptions = BuildOptions(dbConfig);
                InitCache(dbConfig);

                // ReSharper disable once VirtualMemberCallInConstructor
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Loading DB {Name.PadRight(13)} from {_fullPath} with max memory footprint of {_maxThisDbSize / 1000 / 1000}MB");
                var db = _dbsByPath.GetOrAdd(_fullPath, Open, (DbOptions, columnFamilies));

                if (dbConfig.EnableMetricsUpdater)
                {
                    new DbMetricsUpdater(Name, DbOptions, db, dbConfig, _logger).StartUpdating();
                }

                return db;
            }
            catch (DllNotFoundException e) when (e.Message.Contains("libdl"))
            {
                throw new ApplicationException(
                    $"Unable to load 'libdl' necessary to init the RocksDB database. Please run{Environment.NewLine}" +
                    $"sudo apt-get update && sudo apt-get install libsnappy-dev libc6-dev libc6 unzip{Environment.NewLine}" +
                    "or similar depending on your distribution.");
            }
            catch (RocksDbException x) when (x.Message.Contains("LOCK"))
            {
                if (_logger.IsWarn)
                    _logger.Warn(
                        "If your database did not close properly you need to call 'find -type f -name '*LOCK*' -delete' from the databse folder");
                throw;
            }
        }

        protected internal void UpdateReadMetrics()
        {
            if (_settings.UpdateReadMetrics != null)
                _settings.UpdateReadMetrics?.Invoke();
            else
                Metrics.OtherDbReads++;
        }

        protected internal void UpdateWriteMetrics()
        {
            if (_settings.UpdateWriteMetrics != null)
                _settings.UpdateWriteMetrics?.Invoke();
            else
                Metrics.OtherDbWrites++;
        }

        private T ReadConfig<T>(IDbConfig dbConfig, string propertyName)
        {
            return ReadConfig<T>(dbConfig, propertyName, Name);
        }

        protected static T ReadConfig<T>(IDbConfig dbConfig, string propertyName, string tableName)
        {
            string prefixed = string.Concat(tableName == "State" ? string.Empty : string.Concat(tableName, "Db"),
                propertyName);
            try
            {
                return (T)dbConfig.GetType()
                    .GetProperty(prefixed, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(dbConfig);
            }
            catch (Exception e)
            {
                throw new InvalidDataException($"Unable to read {prefixed} property from DB config", e);
            }
        }

        protected virtual DbOptions BuildOptions(IDbConfig dbConfig)
        {
            _maxThisDbSize = 0;
            BlockBasedTableOptions tableOptions = new();
            tableOptions.SetBlockSize(16 * 1024);
            tableOptions.SetPinL0FilterAndIndexBlocksInCache(true);
            tableOptions.SetCacheIndexAndFilterBlocks(GetCacheIndexAndFilterBlocks(dbConfig));

            tableOptions.SetFilterPolicy(BloomFilterPolicy.Create(10, true));
            tableOptions.SetFormatVersion(4);

            ulong blockCacheSize = GetBlockCacheSize(dbConfig);

            tableOptions.SetBlockCache(_cache);

            // IntPtr cache = RocksDbSharp.Native.Instance.rocksdb_cache_create_lru(new UIntPtr(blockCacheSize));
            // tableOptions.SetBlockCache(cache);

            DbOptions options = new();
            options.SetCreateIfMissing(true);
            options.SetAdviseRandomOnOpen(true);
            options.OptimizeForPointLookup(
                blockCacheSize); // I guess this should be the one option controlled by the DB size property - bind it to LRU cache size
            //options.SetCompression(CompressionTypeEnum.rocksdb_snappy_compression);
            //options.SetLevelCompactionDynamicLevelBytes(true);

            /*
             * Multi-Threaded Compactions
             * Compactions are needed to remove multiple copies of the same key that may occur if an application overwrites an existing key. Compactions also process deletions of keys. Compactions may occur in multiple threads if configured appropriately.
             * The entire database is stored in a set of sstfiles. When a memtable is full, its content is written out to a file in Level-0 (L0). RocksDB removes duplicate and overwritten keys in the memtable when it is flushed to a file in L0. Some files are periodically read in and merged to form larger files - this is called compaction.
             * The overall write throughput of an LSM database directly depends on the speed at which compactions can occur, especially when the data is stored in fast storage like SSD or RAM. RocksDB may be configured to issue concurrent compaction requests from multiple threads. It is observed that sustained write rates may increase by as much as a factor of 10 with multi-threaded compaction when the database is on SSDs, as compared to single-threaded compactions.
             * TKS: Observed 500MB/s compared to ~100MB/s between multithreaded and single thread compactions on my machine (processor count is returning 12 for 6 cores with hyperthreading)
             * TKS: CPU goes to insane 30% usage on idle - compacting only app
             */
            options.SetMaxBackgroundCompactions(Environment.ProcessorCount);

            //options.SetMaxOpenFiles(32);
            ulong writeBufferSize = GetWriteBufferSize(dbConfig);
            options.SetWriteBufferSize(writeBufferSize);
            int writeBufferNumber = (int)GetWriteBufferNumber(dbConfig);
            options.SetMaxWriteBufferNumber(writeBufferNumber);
            options.SetMinWriteBufferNumberToMerge(2);

            lock (_dbsByPath)
            {
                _maxThisDbSize += (long)writeBufferSize * writeBufferNumber;
                Interlocked.Add(ref _maxRocksSize, _maxThisDbSize);
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Expected max memory footprint of {Name} DB is {_maxThisDbSize / 1000 / 1000}MB ({writeBufferNumber} * {writeBufferSize / 1000 / 1000}MB + {blockCacheSize / 1000 / 1000}MB)");
                if (_logger.IsDebug) _logger.Debug($"Total max DB footprint so far is {_maxRocksSize / 1000 / 1000}MB");
                ThisNodeInfo.AddInfo("Mem est DB   :", $"{_maxRocksSize / 1000 / 1000}MB".PadLeft(8));
            }

            options.SetBlockBasedTableFactory(tableOptions);

            options.SetMaxBackgroundFlushes(Environment.ProcessorCount);
            options.IncreaseParallelism(Environment.ProcessorCount);
            options.SetRecycleLogFileNum(dbConfig
                .RecycleLogFileNum); // potential optimization for reusing allocated log files

//            options.SetLevelCompactionDynamicLevelBytes(true); // only switch on on empty DBs
            WriteOptions = new WriteOptions();
            WriteOptions.SetSync(dbConfig
                .WriteAheadLogSync); // potential fix for corruption on hard process termination, may cause performance degradation

            if (dbConfig.EnableDbStatistics)
            {
                options.EnableStatistics();
            }
            options.SetStatsDumpPeriodSec(dbConfig.StatsDumpPeriodSec);

            return options;
        }

        private bool GetCacheIndexAndFilterBlocks(IDbConfig dbConfig)
        {
            return _settings.CacheIndexAndFilterBlocks.HasValue
                ? _settings.CacheIndexAndFilterBlocks.Value
                : ReadConfig<bool>(dbConfig, nameof(dbConfig.CacheIndexAndFilterBlocks));
        }

        private ulong GetBlockCacheSize(IDbConfig dbConfig)
        {
            return _settings.BlockCacheSize.HasValue
                ? _settings.BlockCacheSize.Value
                : ReadConfig<ulong>(dbConfig, nameof(dbConfig.BlockCacheSize));
        }

        private ulong GetWriteBufferSize(IDbConfig dbConfig)
        {
            return _settings.WriteBufferSize.HasValue
                ? _settings.WriteBufferSize.Value
                : ReadConfig<ulong>(dbConfig, nameof(dbConfig.WriteBufferSize));
        }

        private ulong GetWriteBufferNumber(IDbConfig dbConfig)
        {
            return _settings.WriteBufferNumber.HasValue
                ? _settings.WriteBufferNumber.Value
                : ReadConfig<uint>(dbConfig, nameof(dbConfig.WriteBufferNumber));
        }


        public byte[]? this[byte[] key]
        {
            get
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
                }

                UpdateReadMetrics();
                return _db.Get(key);
            }
            set
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException($"Attempted to write to a disposed database {Name}");
                }

                UpdateWriteMetrics();

                if (value == null)
                {
                    _db.Remove(key, null, WriteOptions);
                }
                else
                {
                    _db.Put(key, value, null, WriteOptions);
                }
            }
        }

        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => _db.MultiGet(keys);

        public Span<byte> GetSpan(byte[] key)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
            }

            UpdateReadMetrics();
            return _db.GetSpan(key);
        }

        public void DangerousReleaseMemory(in Span<byte> span)
        {
            _db.DangerousReleaseMemory(in span);
        }

        public void Remove(byte[] key)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to delete form a disposed database {Name}");
            }

            _db.Remove(key, null, WriteOptions);
        }

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to create an iterator on a disposed database {Name}");
            }

            Iterator iterator = CreateIterator(ordered);
            return GetAllCore(iterator);
        }

        protected internal Iterator CreateIterator(bool ordered = false, ColumnFamilyHandle? ch = null)
        {
            ReadOptions readOptions = new();
            readOptions.SetTailing(!ordered);
            return _db.NewIterator(ch, readOptions);
        }

        public IEnumerable<byte[]> GetAllValues(bool ordered = false)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
            }

            Iterator iterator = CreateIterator(ordered);
            return GetAllValuesCore(iterator);
        }

        internal IEnumerable<byte[]> GetAllValuesCore(Iterator iterator)
        {
            iterator.SeekToFirst();
            while (iterator.Valid())
            {
                yield return iterator.Value();
                iterator.Next();
            }

            iterator.Dispose();
        }

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAllCore(Iterator iterator)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
            }

            iterator.SeekToFirst();
            while (iterator.Valid())
            {
                yield return new KeyValuePair<byte[], byte[]>(iterator.Key(), iterator.Value());
                iterator.Next();
            }

            iterator.Dispose();
        }

        public bool KeyExists(byte[] key)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
            }

            // seems it has no performance impact
            return _db.Get(key) != null;
//            return _db.Get(key, 32, _keyExistsBuffer, 0, 0, null, null) != -1;
        }

        public IDb Innermost => this;

        public IBatch StartBatch()
        {
            IBatch batch = new RocksDbBatch(this);
            _currentBatches.Add(batch);
            return batch;
        }

        internal class RocksDbBatch : IBatch
        {
            private readonly DbOnTheRocks _dbOnTheRocks;

            internal readonly WriteBatch _rocksBatch;

            public RocksDbBatch(DbOnTheRocks dbOnTheRocks)
            {
                _dbOnTheRocks = dbOnTheRocks;

                if (_dbOnTheRocks._isDisposed)
                {
                    throw new ObjectDisposedException($"Attempted to create a batch on a disposed database {_dbOnTheRocks.Name}");
                }

                _rocksBatch = new WriteBatch();
            }

            public void Dispose()
            {
                if (_dbOnTheRocks._isDisposed)
                {
                    throw new ObjectDisposedException($"Attempted to commit a batch on a disposed database {_dbOnTheRocks.Name}");
                }

                _dbOnTheRocks._db.Write(_rocksBatch, _dbOnTheRocks.WriteOptions);
                _dbOnTheRocks._currentBatches.Remove(this);
                _rocksBatch.Dispose();
                GC.SuppressFinalize(this);
            }

            public byte[]? this[byte[] key]
            {
                get => _dbOnTheRocks[key];
                set
                {
                    if (value == null)
                    {
                        _rocksBatch.Delete(key);
                    }
                    else
                    {
                        _rocksBatch.Put(key, value);
                    }
                }
            }
        }

        public void Flush()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to flush a disposed database {Name}");
            }

            RocksDbSharp.Native.Instance.rocksdb_flush(_db.Handle, FlushOptions.DefaultFlushOptions.Handle);
        }

        public void Clear()
        {
            try
            {
                Directory.Delete(_fullPath!, true);
            }
            catch (Exception e)
            {
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"This is not a problem but I could not delete the pending tx database on startup. {e.Message}");
            }
        }

        private class FlushOptions
        {
            internal static FlushOptions DefaultFlushOptions { get; } = new();

            public FlushOptions()
            {
                Handle = RocksDbSharp.Native.Instance.rocksdb_flushoptions_create();
            }

            public IntPtr Handle { get; protected set; }

            ~FlushOptions()
            {
                if (Handle != IntPtr.Zero)
                {
                    RocksDbSharp.Native.Instance.rocksdb_flushoptions_destroy(Handle);
                    Handle = IntPtr.Zero;
                }
            }
        }

        private void ReleaseUnmanagedResources()
        {
            // ReSharper disable once ConstantConditionalAccessQualifier
            // running in finalizer, potentially not fully constructed
            _db?.Dispose();
            foreach (IBatch batch in _currentBatches)
            {
                batch.Dispose();
            }
        }

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                // ReSharper disable once ConstantConditionalAccessQualifier
                _logger?.Info($"Disposing DB {Name}");

                if (disposing)
                {
                    Flush();
                }

                _isDisposed = true;

                ReleaseUnmanagedResources();
                if (disposing)
                {
                    _dbsByPath.Remove(_fullPath!, out _);
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~DbOnTheRocks()
        {
            Dispose(false);
        }
    }
}
