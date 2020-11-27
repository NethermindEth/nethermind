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
using Nethermind.Logging;

namespace Nethermind.Db.Rocks
{
    public class SimpleRocksDb : DbOnTheRocks
    {
        private readonly Action _updateReadMetrics;
        private readonly Action _updateWriteMetrics;
        public override string Name { get; protected set; } = "SimpleRocksDb";
        public SimpleRocksDb(
            string basePath, 
            string dbPath, 
            string dbName, 
            IPlugableDbConfig dbConfig, 
            ILogManager logManager = null, 
            Action updateReadMetrics = null, 
            Action updateWriteMetrics = null)
                : base(basePath, dbPath, dbName, dbConfig, logManager)
        {
            _updateReadMetrics = updateReadMetrics;
            _updateWriteMetrics = updateWriteMetrics;
        }

        protected internal override void UpdateReadMetrics()
        {
            if (_updateReadMetrics != null)
                _updateReadMetrics?.Invoke();
            else
                Metrics.OtherDbReads++;
        }

        protected internal override void UpdateWriteMetrics()
        {
            if (_updateWriteMetrics != null)
                _updateWriteMetrics?.Invoke();
            else
                Metrics.OtherDbWrites++;
        }
    }
}
