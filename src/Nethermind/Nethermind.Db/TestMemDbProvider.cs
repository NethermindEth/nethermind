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
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Nethermind.Db
{
    public class TestMemDbProvider : IDbProvider
    {
        public static async Task<IDbProvider> InitAsync()
        {
            IDbProvider memDbProvider = new DbProvider(DbModeHint.Mem);
            var standardDbInitializer = new StandardDbInitializer(memDbProvider, null, new MemDbFactory());
            await standardDbInitializer.InitStandardDbsAsync(true);
            return memDbProvider;
        }

        public static IDbProvider Init()
        {
            IDbProvider memDbProvider = new DbProvider(DbModeHint.Mem);
            var standardDbInitializer = new StandardDbInitializer(memDbProvider, null, new MemDbFactory());
            standardDbInitializer.InitStandardDbs(true);
            return memDbProvider;
        }

        public ISnapshotableDb StateDb { get; } = new StateDb();
        public ISnapshotableDb CodeDb { get; } = new StateDb();
        public IColumnsDb<ReceiptsColumns> ReceiptsDb { get; } = new MemColumnsDb<ReceiptsColumns>();
        public IDb BlocksDb { get; } = new MemDb();
        public IDb HeadersDb { get; } = new MemDb();
        public IDb BlockInfosDb { get; } = new MemDb();
        public IDb PendingTxsDb { get; } = new MemDb();
        public IDb ConfigsDb { get; } = new MemDb();
        public IDb EthRequestsDb { get; } = new MemDb();
        public IDb BloomDb { get; } = new MemDb();
        public IDb ChtDb { get; } = new MemDb();
        public IDb BeamStateDb { get; } = new MemDb();

        public DbModeHint DbMode => DbModeHint.Mem;

        public IDictionary<string, IDb> RegisteredDbs => throw new NotImplementedException();

        public void Dispose()
        {
            StateDb?.Dispose();
            CodeDb?.Dispose();
            ReceiptsDb?.Dispose();
            BlocksDb?.Dispose();
            BlockInfosDb?.Dispose();
            PendingTxsDb?.Dispose();
            ConfigsDb?.Dispose();
            EthRequestsDb?.Dispose();
            BloomDb?.Dispose();
            ChtDb?.Dispose();
        }

        public T GetDb<T>(string dbName) where T : IDb
        {
            throw new NotImplementedException();
        }

        public void RegisterDb<T>(string dbName, T db) where T : IDb
        {
            throw new NotImplementedException();
        }
    }
}
