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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using RocksDbSharp;

namespace Nethermind.Db.Rocks
{
    public abstract class ColumnsDb<T> : DbOnTheRocks, IColumnsDb<T>
    {
        private readonly IDictionary<T, IDb> _columnDbs = new Dictionary<T, IDb>();
        
        protected ColumnsDb(string basePath, string dbPath, IDbConfig dbConfig, ILogManager logManager, params T[] keys) : base(basePath, dbPath, dbConfig, logManager, GetColumnFamilies(keys))
        {
            keys = GetEnumKeys(keys);

            foreach (var key in keys)
            {
                _columnDbs[key] = new ColumnDb(Db, this, key.ToString()); 
            }
        }

        private static T[] GetEnumKeys(T[] keys)
        {
            if (typeof(T).IsEnum && keys.Length == 0)
            {
                keys = Enum.GetValues(typeof(T)).Cast<T>().ToArray();
            }

            return keys;
        }

        private static ColumnFamilies GetColumnFamilies(T[] keys)
        {
            var result = new ColumnFamilies();
            foreach (var key in keys)
            {
                result.Add(key.ToString(), new ColumnFamilyOptions());
            }
            return result;
        }

        protected override DbOptions BuildOptions(IDbConfig dbConfig)
        {
            var options = base.BuildOptions(dbConfig);
            options.SetCreateMissingColumnFamilies();
            return options;
        }

        public IDb GetColumnDb(T key) => _columnDbs[key];

        public IEnumerable<T> ColumnKeys => _columnDbs.Keys;
    }
}