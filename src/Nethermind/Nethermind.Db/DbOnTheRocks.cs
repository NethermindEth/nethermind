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
using System.IO;
using Nethermind.Store;
using RocksDbSharp;

namespace Nethermind.Db
{
    public class DbOnTheRocks : IDb
    {
        public const string StorageDbPath = "state";
        public const string StateDbPath = "state";
        public const string CodeDbPath = "code";
        public const string BlocksDbPath = "blocks";
        public const string ReceiptsDbPath = "receipts";
        public const string BlockInfosDbPath = "blockInfos";

        private static readonly ConcurrentDictionary<string, RocksDb> DbsByPath = new ConcurrentDictionary<string, RocksDb>();

        private readonly RocksDb _db;

        private readonly DbInstance _dbInstance;

        private WriteBatch _currentBatch;

        public DbOnTheRocks(string dbPath) // TODO: check column families
        {
            if (!Directory.Exists(dbPath))
            {
                Directory.CreateDirectory(dbPath);
            }

            // options are based mainly from EtheruemJ at the moment

            BlockBasedTableOptions tableOptions = new BlockBasedTableOptions();
            //tableOptions.SetPinL0FilterAndIndexBlocksInCache(true);
            tableOptions.SetBlockSize(16 * 1024);
            //tableOptions.SetCacheIndexAndFilterBlocks(true);
            tableOptions.SetFilterPolicy(BloomFilterPolicy.Create(10, false));
            tableOptions.SetFormatVersion(2);

            DbOptions options = new DbOptions();
            options.SetCreateIfMissing(true);
            options.OptimizeForPointLookup(32);
            //options.SetCompression(CompressionTypeEnum.rocksdb_snappy_compression);
            //options.SetLevelCompactionDynamicLevelBytes(true);
            //options.SetMaxBackgroundCompactions(4);
            //options.SetMaxBackgroundFlushes(2);
            //options.SetMaxOpenFiles(32);
            //options.SetDbWriteBufferSize(1024 * 1024 * 16);
            options.SetWriteBufferSize(1024 * 1024 * 16);
            options.SetMaxWriteBufferNumber(6);
            options.SetMinWriteBufferNumberToMerge(2);
            options.SetBlockBasedTableFactory(tableOptions);

            //SliceTransform transform = SliceTransform.CreateFixedPrefix(16);
            //options.SetPrefixExtractor(transform);

            _db = DbsByPath.GetOrAdd(dbPath, path => RocksDb.Open(options, Path.Combine("db", path)));

            if (dbPath.EndsWith(StateDbPath))
            {
                _dbInstance = DbInstance.State;
            }
            else if (dbPath.EndsWith(BlockInfosDbPath))
            {
                _dbInstance = DbInstance.BlockInfo;
            }
            else if (dbPath.EndsWith(BlocksDbPath))
            {
                _dbInstance = DbInstance.Block;
            }
            else if (dbPath.EndsWith(CodeDbPath))
            {
                _dbInstance = DbInstance.Code;
            }
            else if (dbPath.EndsWith(ReceiptsDbPath))
            {
                _dbInstance = DbInstance.Receipts;
            }
            else
            {
                _dbInstance = DbInstance.Other;
            }
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
                    case DbInstance.Storage:
                        Metrics.StorageDbReads++;
                        break;
                    case DbInstance.BlockInfo:
                        Metrics.BlockInfosDbReads++;
                        break;
                    case DbInstance.Block:
                        Metrics.BlocksDbReads++;
                        break;
                    case DbInstance.Code:
                        Metrics.CodeDbReads++;
                        break;
                    case DbInstance.Receipts:
                        Metrics.ReceiptsDbReads++;
                        break;
                    case DbInstance.Other:
                        Metrics.OtherDbReads++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (_currentBatch != null)
                {
                    throw new NotSupportedException("Index not needed, am I right?");
                    //return _currentBatch.Get(key);
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
                    case DbInstance.Storage:
                        Metrics.StorageDbWrites++;
                        break;
                    case DbInstance.BlockInfo:
                        Metrics.BlockInfosDbWrites++;
                        break;
                    case DbInstance.Block:
                        Metrics.BlocksDbWrites++;
                        break;
                    case DbInstance.Code:
                        Metrics.CodeDbWrites++;
                        break;
                    case DbInstance.Receipts:
                        Metrics.ReceiptsDbWrites++;
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
            Storage,
            BlockInfo,
            Block,
            Code,
            Receipts,
            Other
        }
    }
}