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

using System.Collections.Concurrent;
using System.IO;
using Nethermind.Core.Extensions;
using Nethermind.Store;
using RocksDbSharp;

namespace Nethermind.Db
{
    public class DbOnTheRocks : IDb
    {
        public const string StorageDbPath = "storage";
        public const string StateDbPath = "state";
        public const string CodeDbPath = "code";
        public const string BlocksDbPath = "blocks";
        public const string ReceiptsDbPath = "receipts";
        public const string BlockInfosDbPath = "blockInfos";

        private static readonly ConcurrentDictionary<string, RocksDb> DbsByPath = new ConcurrentDictionary<string, RocksDb>();
        
        private readonly RocksDb _db;
        private readonly byte[] _prefix;

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
        }

        public byte[] this[byte[] key]
        {
            get => _db.Get(_prefix == null ? key : Bytes.Concat(_prefix, key));
            set => _db.Put(_prefix == null ? key : Bytes.Concat(_prefix, key), value);
        }

        public void Remove(byte[] key)
        {
            _db.Remove(_prefix == null ? key : Bytes.Concat(_prefix, key));
        }
    }
}