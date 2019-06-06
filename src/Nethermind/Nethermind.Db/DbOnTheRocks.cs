/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;
using RocksDbSharp;

namespace Nethermind.Db
{
    public class DbOnTheRocks : IDb
    {
        public const string StateDbPath = "state";
        public const string CodeDbPath = "code";
        public const string BlocksDbPath = "blocks";
        public const string HeadersPath = "headers";
        public const string ReceiptsDbPath = "receipts";
        public const string BlockInfosDbPath = "blockInfos";
        public const string PendingTxsDbPath = "pendingtxs";
        public const string TraceDbPath = "trace";

        private static readonly ConcurrentDictionary<string, RocksDb> DbsByPath = new ConcurrentDictionary<string, RocksDb>();

        private readonly RocksDb _db;

        private readonly DbInstance _dbInstance;

        private WriteBatch _currentBatch;

        public DbOnTheRocks(string dbPath, IDbConfig dbConfig, ILogManager logManager = null) // TODO: check column families
        {
            ILogger logger = logManager?.GetClassLogger();
            if (!Directory.Exists(dbPath))
            {
                Directory.CreateDirectory(dbPath);
            }

            if (logger != null)
            {
                if (logger.IsInfo) logger.Info($"Using database directory {dbPath}");
            }

            if (dbPath.EndsWith(StateDbPath))
            {
                _dbInstance = DbInstance.State;
            }
            else if (dbPath.EndsWith(BlockInfosDbPath))
            {
                _dbInstance = DbInstance.BlockInfos;
            }
            else if (dbPath.EndsWith(HeadersPath))
            {
                _dbInstance = DbInstance.Headers;
            }
            else if (dbPath.EndsWith(BlocksDbPath))
            {
                _dbInstance = DbInstance.Blocks;
            }
            else if (dbPath.EndsWith(CodeDbPath))
            {
                _dbInstance = DbInstance.Code;
            }
            else if (dbPath.EndsWith(ReceiptsDbPath))
            {
                _dbInstance = DbInstance.Receipts;
            }
            else if (dbPath.EndsWith(PendingTxsDbPath))
            {
                _dbInstance = DbInstance.PendingTxs;
            }
            else if (dbPath.EndsWith(TraceDbPath))
            {
                _dbInstance = DbInstance.Trace;
            }
            else
            {
                _dbInstance = DbInstance.Other;
            }

            DbOptions options = BuildOptions(dbConfig, _dbInstance);
            _db = DbsByPath.GetOrAdd(dbPath, path => RocksDb.Open(options, path));
        }

        private static T ReadConfig<T>(IDbConfig dbConfig, DbInstance dbInstance, string propertyName)
        {
            string prefixed = string.Concat(dbInstance == DbInstance.State ? string.Empty : string.Concat(dbInstance.ToString(), "Db"), propertyName);
            try
            {
                return (T) typeof(IDbConfig).GetProperty(prefixed, BindingFlags.Public | BindingFlags.Instance).GetValue(dbConfig);
            }
            catch (Exception e)
            {
                throw new InvalidDataException($"Unable to read {prefixed} property from DB config", e);
            }
        }

        private DbOptions BuildOptions(IDbConfig dbConfig, DbInstance dbInstance)
        {
            BlockBasedTableOptions tableOptions = new BlockBasedTableOptions();
            tableOptions.SetBlockSize(16 * 1024);
            tableOptions.SetPinL0FilterAndIndexBlocksInCache(true);
            tableOptions.SetCacheIndexAndFilterBlocks(ReadConfig<bool>(dbConfig, dbInstance, nameof(dbConfig.CacheIndexAndFilterBlocks)));

            tableOptions.SetFilterPolicy(BloomFilterPolicy.Create(10, true));
            tableOptions.SetFormatVersion(2);

            ulong blockCacheSize = ReadConfig<ulong>(dbConfig, dbInstance, nameof(dbConfig.BlockCacheSize));
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
            options.SetWriteBufferSize(ReadConfig<ulong>(dbConfig, dbInstance, nameof(dbConfig.WriteBufferSize)));
            options.SetMaxWriteBufferNumber((int)ReadConfig<uint>(dbConfig, dbInstance, nameof(dbConfig.WriteBufferNumber)));
            options.SetMinWriteBufferNumberToMerge(2);
            options.SetBlockBasedTableFactory(tableOptions);
            
            options.SetMaxBackgroundFlushes(Environment.ProcessorCount);
            options.IncreaseParallelism(Environment.ProcessorCount);
//            options.SetLevelCompactionDynamicLevelBytes(true); // only switch on on empty DBs
            return options;
        }

        public byte[] this[byte[] key]
        {
            get
            {
                switch (_dbInstance)
                {
                    case DbInstance.State:
                        Metrics.StateDbReads++;
                        break;
                    case DbInstance.BlockInfos:
                        Metrics.BlockInfosDbReads++;
                        break;
                    case DbInstance.Blocks:
                        Metrics.BlocksDbReads++;
                        break;
                    case DbInstance.Code:
                        Metrics.CodeDbReads++;
                        break;
                    case DbInstance.Headers:
                        Metrics.HeaderDbReads++;
                        break;
                    case DbInstance.Receipts:
                        Metrics.ReceiptsDbReads++;
                        break;
                    case DbInstance.PendingTxs:
                        Metrics.PendingTxsDbReads++;
                        break;
                    case DbInstance.Trace:
                        Metrics.TraceDbReads++;
                        break;
                    case DbInstance.Other:
                        Metrics.OtherDbReads++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                return _db.Get(key);
            }
            set
            {
                switch (_dbInstance)
                {
                    case DbInstance.State:
                        Metrics.StateDbWrites++;
                        break;
                    case DbInstance.BlockInfos:
                        Metrics.BlockInfosDbWrites++;
                        break;
                    case DbInstance.Blocks:
                        Metrics.BlocksDbWrites++;
                        break;
                    case DbInstance.Code:
                        Metrics.CodeDbWrites++;
                        break;
                    case DbInstance.Headers:
                        Metrics.HeaderDbWrites++;
                        break;
                    case DbInstance.Receipts:
                        Metrics.ReceiptsDbWrites++;
                        break;
                    case DbInstance.PendingTxs:
                        Metrics.PendingTxsDbWrites++;
                        break;
                    case DbInstance.Trace:
                        Metrics.TraceDbWrites++;
                        break;
                    case DbInstance.Other:
                        Metrics.OtherDbWrites++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (_currentBatch != null)
                {
                    if (value == null)
                    {
                        _currentBatch.Delete(key);
                    }
                    else
                    {
                        _currentBatch.Put(key, value);
                    }
                }
                else
                {
                    if (value == null)
                    {
                        _db.Remove(key);
                    }
                    else
                    {
                        _db.Put(key, value);
                    }
                }
            }
        }

        public void Remove(byte[] key)
        {
            _db.Remove(key);
        }

        public byte[][] GetAll()
        {
            var iterator = _db.NewIterator();
            iterator = iterator.SeekToFirst();
            var values = new List<byte[]>();
            while (iterator.Valid())
            {
                values.Add(iterator.Value());
                iterator = iterator.Next();
            }

            iterator.Dispose();

            return values.ToArray();
        }

        private byte[] _keyExistsBuffer = new byte[1];
        
        public bool KeyExists(byte[] key)
        {
            // seems it has no performance impact
            return _db.Get(key) != null;
//            return _db.Get(key, 32, _keyExistsBuffer, 0, 0, null, null) != -1;
        }
        
        public void StartBatch()
        {
            _currentBatch = new WriteBatch();
        }

        public void CommitBatch()
        {
            _db.Write(_currentBatch);
            _currentBatch.Dispose();
            _currentBatch = null;
        }

        private enum DbInstance
        {
            State,
            BlockInfos,
            Blocks,
            Code,
            Headers,
            Receipts,
            Trace,
            PendingTxs,
            Other
        }

        public void Dispose()
        {
            _db?.Dispose();
            _currentBatch?.Dispose();
        }
    }
}