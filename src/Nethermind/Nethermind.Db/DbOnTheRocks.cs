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
using Nethermind.Core.Extensions;
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
        public const string PeersDbPath = "peers";

        private static readonly ConcurrentDictionary<string, RocksDb> DbsByPath = new ConcurrentDictionary<string, RocksDb>();

        private readonly RocksDb _db;
        private readonly DbInstance _dbInstance;
        private readonly byte[] _prefix;

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

        public DbOnTheRocks(string dbPath, byte[] prefix = null) // TODO: check column families
        {
            if (!Directory.Exists("db"))
            {
                Directory.CreateDirectory("db");
            }

            _prefix = prefix;
            DbOptions options = new DbOptions();
            options.SetCreateIfMissing(true);

            _db = DbsByPath.GetOrAdd(dbPath, path => RocksDb.Open(options, Path.Combine("db", path)));

            if (dbPath.EndsWith(StateDbPath))
            {
                _dbInstance = _prefix == null ? DbInstance.State : DbInstance.Storage;
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

                return _db.Get(_prefix == null ? key : Bytes.Concat(_prefix, key));
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

                _db.Put(_prefix == null ? key : Bytes.Concat(_prefix, key), value);
            }
        }

        public void Remove(byte[] key)
        {
            _db.Remove(_prefix == null ? key : Bytes.Concat(_prefix, key));
        }

        public ICollection<byte[]> Keys
        {
            get { return GetKeysOrValues(x => x.Key()); }
        }

        public ICollection<byte[]> Values
        {
            get { return GetKeysOrValues(x => x.Value()); }
        }

        private ICollection<byte[]> GetKeysOrValues(Func<Iterator, byte[]> selector)
        {
            var readOptions = new ReadOptions();
            var items = new List<byte[]>();
            using (var iter = _db.NewIterator(readOptions: readOptions))
            {
                while (iter.Valid())
                {
                    var item = selector.Invoke(iter);
                    items.Add(item);
                    iter.Next();
                }
            }

            return items;
        }
    }
}