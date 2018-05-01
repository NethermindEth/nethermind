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

using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Store;
using RocksDbSharp;

namespace Nethermind.Db
{
    public class KeyValueDb : IDb
    {
        public const string BlocksDbPath = "blocks";
        public const string BlockInfosDbPath = "blockInfos";
        
        private readonly RocksDb _db;

        public KeyValueDb(string dbPath)
        {
            DbOptions options = new DbOptions();
            _db = RocksDb.Open(options, Path.Combine("db", dbPath));
        }

        public byte[] this[Keccak key]
        {
            get => _db.Get(key.Bytes);
            set => _db.Put(key.Bytes, value);
        }

        public bool ContainsKey(Keccak key)
        {
            return _db.Get(key.Bytes) != null;
        }

        public void Remove(Keccak key)
        {
            _db.Remove(key.Bytes);
        }
    }
}