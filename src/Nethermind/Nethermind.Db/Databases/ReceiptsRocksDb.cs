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
using System.Collections;
using System.Collections.Generic;
using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db.Databases
{
    public class ReceiptsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "Receipts";

        public ReceiptsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "receipts", dbConfig, logManager)
        {
        }

        internal override void UpdateReadMetrics() => Metrics.ReceiptsDbReads++;
        internal override void UpdateWriteMetrics() => Metrics.ReceiptsDbWrites++;
    }
    
    public class BloomRocksDb : DbOnTheRocks, IColumnDb<byte>
    {
        public override string Name { get; } = "Bloom";
        
        private readonly IDictionary<byte, IDb> _columnDbs = new Dictionary<byte, IDb>();

        public BloomRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "bloom", dbConfig, logManager)
        {
        }
        
        public IDb GetColumnDb(byte key)
        {
            if (!_columnDbs.TryGetValue(key, out var db))
            {
                _columnDbs[key] = db = new ColumnDb(Db, this, key.ToString());
            }

            return db;
        }

        internal override void UpdateReadMetrics() => Metrics.BloomDbReads++;
        internal override void UpdateWriteMetrics() => Metrics.BloomDbWrites++;
    }
}