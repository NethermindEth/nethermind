//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Logging;
using RocksDbSharp;

namespace Nethermind.Db.Rocks
{
    public abstract class DbOnTheRocks : IDbWithSpan
    {
        private static readonly ConcurrentDictionary<string, RocksDb> DbsByPath = new ConcurrentDictionary<string, RocksDb>();
        internal readonly RocksDb Db;
        internal WriteBatch CurrentBatch { get; private set; }
        internal WriteOptions WriteOptions { get; private set; }

        public abstract string Name { get; }

        private static long _maxRocksSize;

        private long _maxThisDbSize;

        public DbOnTheRocks(string basePath, string dbPath, IDbConfig dbConfig, ILogManager logManager, ColumnFamilies columnFamilies = null, bool deleteOnStart = false)
        {
            RocksDb Open(DbOptions options, string path, ColumnFamilies families)
            {
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
                DbOptions options = BuildOptions(dbConfig);

                // ReSharper disable once VirtualMemberCallInConstructor
                if (_logger.IsDebug) _logger.Debug($"Loading DB {Name.PadRight(13)} from {_fullPath} with max memory footprint of {_maxThisDbSize / 1024 / 1024}MB");
                Db = DbsByPath.GetOrAdd(_fullPath, path => Open(options, path, columnFamilies));
            }
            catch (DllNotFoundException e) when (e.Message.Contains("libdl"))
            {
                throw new ApplicationException($"Unable to load 'libdl' necessary to init the RocksDB database. Please run{Environment.NewLine}" +
                                               "sudo apt update && sudo apt install libsnappy-dev libc6-dev libc6");
            }
        }

        protected internal virtual void UpdateReadMetrics() => Metrics.OtherDbReads++;
        protected internal virtual void UpdateWriteMetrics() => Metrics.OtherDbWrites++;

        private T ReadConfig<T>(IDbConfig dbConfig, string propertyName)
        {
            return ReadConfig<T>(dbConfig, propertyName, Name);
        }

        protected static T ReadConfig<T>(IDbConfig dbConfig, string propertyName, string tableName)
        {
            string prefixed = string.Concat(tableName == "State" ? string.Empty : string.Concat(tableName, "Db"), propertyName);
            try
            {
                return (T) dbConfig.GetType().GetProperty(prefixed, BindingFlags.Public | BindingFlags.Instance).GetValue(dbConfig);
            }
            catch (Exception e)
            {
                throw new InvalidDataException($"Unable to read {prefixed} property from DB config", e);
            }
        }

        protected virtual DbOptions BuildOptions(IDbConfig dbConfig)
        {
            _maxThisDbSize = 0;
            BlockBasedTableOptions tableOptions = new BlockBasedTableOptions();
            tableOptions.SetBlockSize(16 * 1024);
            tableOptions.SetPinL0FilterAndIndexBlocksInCache(true);
            tableOptions.SetCacheIndexAndFilterBlocks(ReadConfig<bool>(dbConfig, nameof(dbConfig.CacheIndexAndFilterBlocks)));

            tableOptions.SetFilterPolicy(BloomFilterPolicy.Create(10, true));
            tableOptions.SetFormatVersion(2);

            ulong blockCacheSize = ReadConfig<ulong>(dbConfig, nameof(dbConfig.BlockCacheSize));
            _maxThisDbSize += (long) blockCacheSize;

            IntPtr cache = Native.Instance.rocksdb_cache_create_lru(new UIntPtr(blockCacheSize));
            tableOptions.SetBlockCache(cache);

            DbOptions options = new DbOptions();
            options.SetCreateIfMissing(true);
            options.SetAdviseRandomOnOpen(true);
            options.OptimizeForPointLookup(blockCacheSize); // I guess this should be the one option controlled by the DB size property - bind it to LRU cache size
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
            ulong writeBufferSize = ReadConfig<ulong>(dbConfig, nameof(dbConfig.WriteBufferSize));
            options.SetWriteBufferSize(writeBufferSize);
            int writeBufferNumber = (int) ReadConfig<uint>(dbConfig, nameof(dbConfig.WriteBufferNumber));
            options.SetMaxWriteBufferNumber(writeBufferNumber);
            options.SetMinWriteBufferNumberToMerge(2);

            lock (DbsByPath)
            {
                _maxThisDbSize += (long) writeBufferSize * writeBufferNumber;
                Interlocked.Add(ref _maxRocksSize, _maxThisDbSize);
                if (_logger.IsDebug) _logger.Debug($"Expected max memory footprint of {Name} DB is {_maxThisDbSize / 1024 / 1024}MB ({writeBufferNumber} * {writeBufferSize / 1024 / 1024}MB + {blockCacheSize / 1024 / 1024}MB)");
                if (_logger.IsDebug) _logger.Debug($"Total max DB footprint so far is {_maxRocksSize / 1024 / 1024}MB");
                ThisNodeInfo.AddInfo("Mem est DB   :", $"{_maxRocksSize / 1024 / 1024}MB".PadLeft(8));
            }

            options.SetBlockBasedTableFactory(tableOptions);

            options.SetMaxBackgroundFlushes(Environment.ProcessorCount);
            options.IncreaseParallelism(Environment.ProcessorCount);
            options.SetRecycleLogFileNum(dbConfig.RecycleLogFileNum); // potential optimization for reusing allocated log files

//            options.SetLevelCompactionDynamicLevelBytes(true); // only switch on on empty DBs
            WriteOptions = new WriteOptions();
            WriteOptions.SetSync(dbConfig.WriteAheadLogSync); // potential fix for corruption on hard process termination, may cause performance degradation

            return options;
        }

        public byte[] this[byte[] key]
        {
            get
            {
                UpdateReadMetrics();
                return Db.Get(key);
            }
            set
            {
                UpdateWriteMetrics();
                if (CurrentBatch != null)
                {
                    if (value == null)
                    {
                        CurrentBatch.Delete(key);
                    }
                    else
                    {
                        CurrentBatch.Put(key, value);
                    }
                }
                else
                {
                    if (value == null)
                    {
                        Db.Remove(key, null, WriteOptions);
                    }
                    else
                    {
                        Db.Put(key, value, null, WriteOptions);
                    }
                }
            }
        }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys] => Db.MultiGet(keys);

        public Span<byte> GetSpan(byte[] key)
        {
            UpdateReadMetrics();
            return Db.GetSpan(key);
        }

        public void DangerousReleaseMemory(in Span<byte> span)
        {
            Db.DangerousReleaseMemory(in span);
        }

        public void Remove(byte[] key)
        {
            Db.Remove(key, null, WriteOptions);
        }

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
        {
            Iterator iterator = CreateIterator(ordered);
            return GetAllCore(iterator);
        }

        protected internal Iterator CreateIterator(bool ordered = false, ColumnFamilyHandle ch = null)
        {
            var readOptions = new ReadOptions();
            readOptions.SetTailing(!ordered);
            return Db.NewIterator(ch, readOptions);
        }

        public IEnumerable<byte[]> GetAllValues(bool ordered = false)
        {
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
            iterator.SeekToFirst();
            while (iterator.Valid())
            {
                yield return new KeyValuePair<byte[], byte[]>(iterator.Key(), iterator.Value());
                iterator.Next();
            }

            iterator.Dispose();
        }


        private ILogger _logger;
        private string _fullPath;

        public bool KeyExists(byte[] key)
        {
            // seems it has no performance impact
            return Db.Get(key) != null;
//            return _db.Get(key, 32, _keyExistsBuffer, 0, 0, null, null) != -1;
        }

        public IDb Innermost => this;

        public void StartBatch()
        {
            CurrentBatch = new WriteBatch();
        }

        public void CommitBatch()
        {
            Db.Write(CurrentBatch, WriteOptions);
            CurrentBatch.Dispose();
            CurrentBatch = null;
        }

        public void Dispose()
        {
            DbsByPath.Remove(_fullPath, out _);
            Db?.Dispose();
            CurrentBatch?.Dispose();
        }

        public void Flush()
        {
            Native.Instance.rocksdb_flush(Db.Handle, FlushOptions.DefaultFlushOptions.Handle);
        }

        public void Clear()
        {
            try
            {
                Directory.Delete(_fullPath, true);
            }
            catch (Exception e)
            {
                if(_logger.IsWarn) _logger.Warn($"This is not a problem but I could not delete the pending tx database on startup. {e.Message}");
            }
        }

        private class FlushOptions
        {
            internal static FlushOptions DefaultFlushOptions { get; } = new FlushOptions();

            public FlushOptions()
            {
                Handle = Native.Instance.rocksdb_flushoptions_create();
            }

            public IntPtr Handle { get; protected set; }

            ~FlushOptions()
            {
                if (Handle != IntPtr.Zero)
                {
                    Native.Instance.rocksdb_flushoptions_destroy(Handle);
                    Handle = IntPtr.Zero;
                }
            }
        }
    }
}