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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using RocksDbSharp;

namespace Nethermind.Db.Rocks
{
    public class ColumnsDb<T> : DbOnTheRocks, IColumnsDb<T> where T : notnull
    {
        private readonly IDictionary<T, IDbWithSpan> _columnDbs = new Dictionary<T, IDbWithSpan>();
        
        public ColumnsDb(string basePath, RocksDbSettings settings, IDbConfig dbConfig, ILogManager logManager, params T[] keys) 
            : base(basePath, settings, dbConfig, logManager, GetColumnFamilies(dbConfig, settings.DbName, GetEnumKeys(keys)))
        {
            keys = GetEnumKeys(keys);

            foreach (T key in keys)
            {
                _columnDbs[key] = new ColumnDb(_db, this, key.ToString()!); 
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

        private static ColumnFamilies GetColumnFamilies(IDbConfig dbConfig, string name, T[] keys)
        {
            InitCache(dbConfig);
            
            ColumnFamilies result = new();
            ulong blockCacheSize = ReadConfig<ulong>(dbConfig, nameof(dbConfig.BlockCacheSize), name);
            foreach (T key in keys)
            {
                ColumnFamilyOptions columnFamilyOptions = new();
                columnFamilyOptions.OptimizeForPointLookup(blockCacheSize);
                columnFamilyOptions.SetBlockBasedTableFactory(
                    new BlockBasedTableOptions()
                        .SetFilterPolicy(BloomFilterPolicy.Create())
                        .SetBlockCache(_cache));
                result.Add(key.ToString(), columnFamilyOptions);
            }
            return result;
        }

        protected override DbOptions BuildOptions(IDbConfig dbConfig)
        {
            DbOptions options = base.BuildOptions(dbConfig);
            options.SetCreateMissingColumnFamilies();
            return options;
        }

        public IDbWithSpan GetColumnDb(T key) => _columnDbs[key];

        public IEnumerable<T> ColumnKeys => _columnDbs.Keys;

        public IReadOnlyDb CreateReadOnly(bool createInMemWriteStore)
        {
            return new ReadOnlyColumnsDb<T>(this, createInMemWriteStore);
        }
    }
}
