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
using System.Threading.Tasks;

namespace Nethermind.Db
{
    public class StandardDbInitializer : RocksDbInitializer
    {
        public StandardDbInitializer(
            IDbProvider? dbProvider, 
            IRocksDbFactory? rocksDbFactory, 
            IMemDbFactory? memDbFactory)
            : base(dbProvider, rocksDbFactory, memDbFactory)
        {
        }

        public void InitStandardDbs(bool useReceiptsDb)
        {
            RegisterAll(useReceiptsDb);
            InitAll();
        }

        public async Task InitStandardDbsAsync(bool useReceiptsDb)
        {
            RegisterAll(useReceiptsDb);
            await InitAllAsync();
        }

        private void RegisterAll(bool useReceiptsDb)
        {
            RegisterDb(BuildRocksDbSettings(DbNames.Blocks, () => Metrics.BlocksDbReads++, () => Metrics.BlocksDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.Headers, () => Metrics.HeaderDbReads++, () => Metrics.HeaderDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.BlockInfos, () => Metrics.BlockInfosDbReads++, () => Metrics.BlockInfosDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.State, () => Metrics.StateDbReads++, () => Metrics.StateDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.Code, () => Metrics.CodeDbReads++, () => Metrics.CodeDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.Bloom, () => Metrics.BloomDbReads++, () => Metrics.BloomDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.CHT, () => Metrics.CHTDbReads++, () => Metrics.CHTDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.Witness, () => Metrics.WitnessDbReads++, () => Metrics.WitnessDbWrites++));
            if (useReceiptsDb)
            {
                RegisterColumnsDb<ReceiptsColumns>(BuildRocksDbSettings(DbNames.Receipts, () => Metrics.ReceiptsDbReads++, () => Metrics.ReceiptsDbWrites++));
            }
            else
            {
                RegisterCustomDb(DbNames.Receipts, () => new ReadOnlyColumnsDb<ReceiptsColumns>(new MemColumnsDb<ReceiptsColumns>(), false));
            }
        }

        private RocksDbSettings BuildRocksDbSettings(string dbName, Action updateReadsMetrics, Action updateWriteMetrics)
        {
            return new(GetTitleDbName(dbName), dbName)
            {
                UpdateReadMetrics = updateReadsMetrics,
                UpdateWriteMetrics = updateWriteMetrics
            };
        }
    }
}
