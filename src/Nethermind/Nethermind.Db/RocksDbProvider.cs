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
using Nethermind.Db.Config;
using Nethermind.Store;

namespace Nethermind.Db
{
    public class RocksDbProvider : IDbProvider
    {
        public RocksDbProvider(string basePath, IDbConfig dbConfig)
        {
            BlocksDb = new DbOnTheRocks(
                Path.Combine(basePath, DbOnTheRocks.BlocksDbPath),
                dbConfig);
            
            BlockInfosDb = new DbOnTheRocks(
                Path.Combine(basePath, DbOnTheRocks.BlockInfosDbPath),
                dbConfig);
            
            ReceiptsDb = new DbOnTheRocks(
                Path.Combine(basePath, DbOnTheRocks.ReceiptsDbPath),
                dbConfig);
            
            StateDb = new StateDb(
                new DbOnTheRocks(Path.Combine(basePath, DbOnTheRocks.StateDbPath), dbConfig));
            
            CodeDb = new StateDb(
                new DbOnTheRocks(Path.Combine(basePath, DbOnTheRocks.CodeDbPath), dbConfig));
            
            PendingTxsDb = new DbOnTheRocks(
                Path.Combine(basePath, DbOnTheRocks.PendingTxsDbPath),
                dbConfig);
        }
        
        public ISnapshotableDb StateDb { get; }
        public ISnapshotableDb CodeDb { get; }
        public IDb ReceiptsDb { get; }
        public IDb BlocksDb { get; }
        public IDb BlockInfosDb { get; }
        public IDb PendingTxsDb { get; }

        public void Dispose()
        {
            StateDb?.Dispose();
            CodeDb?.Dispose();
            ReceiptsDb?.Dispose();
            BlocksDb?.Dispose();
            BlockInfosDb?.Dispose();
            PendingTxsDb?.Dispose();
        }
    }
}