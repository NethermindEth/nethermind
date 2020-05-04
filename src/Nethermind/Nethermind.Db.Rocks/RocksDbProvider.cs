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

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks
{
    public class RocksDbProvider : IDbProvider
    {
        private readonly ILogManager _logManager;
        private readonly bool _addNdmDbs;

        public RocksDbProvider(ILogManager logManager, bool addNdmDbs = true)
        {
            _logManager = logManager;
            _addNdmDbs = addNdmDbs;
        }

        public async Task Init(string basePath, IDbConfig dbConfig, bool useReceiptsDb)
        {
            HashSet<Task> allInitializers = new HashSet<Task>();
            allInitializers.Add(Task.Run(() => BlocksDb = new BlocksRocksDb(basePath, dbConfig, _logManager)));
            allInitializers.Add(Task.Run(() => HeadersDb = new HeadersRocksDb(basePath, dbConfig, _logManager)));
            allInitializers.Add(Task.Run(() => BlockInfosDb = new BlockInfosRocksDb(basePath, dbConfig, _logManager)));
            allInitializers.Add(Task.Run(() => StateDb = new StateDb(new StateRocksDb(basePath, dbConfig, _logManager))));
            allInitializers.Add(Task.Run(() => CodeDb = new StateDb(new CodeRocksDb(basePath, dbConfig, _logManager))));
            allInitializers.Add(Task.Run(() => PendingTxsDb = new PendingTxsRocksDb(basePath, dbConfig, _logManager)));
            allInitializers.Add(Task.Run(() => BloomDb = new BloomRocksDb(basePath, dbConfig, _logManager)));
            allInitializers.Add(Task.Run(() => ChtDb = new CanonicalHashRocksDb(basePath, dbConfig, _logManager)));
            allInitializers.Add(Task.Run(() => ConfigsDb = _addNdmDbs ? new ConfigsRocksDb(basePath, dbConfig, _logManager) : (IDb)new MemDb()));
            allInitializers.Add(Task.Run(() => EthRequestsDb = _addNdmDbs ? new EthRequestsRocksDb(basePath, dbConfig, _logManager) : (IDb)new MemDb()));

            allInitializers.Add(Task.Run(() =>
            {
                if (useReceiptsDb)
                {
                    ReceiptsDb = new ReceiptsRocksDb(basePath, dbConfig, _logManager);
                }
                else
                {
                    ReceiptsDb = new ReadOnlyColumnsDb<ReceiptsColumns>(new MemColumnsDb<ReceiptsColumns>(), false);
                }
            }));

            await Task.WhenAll(allInitializers);
        }

        public ISnapshotableDb StateDb { get; private set; }
        public ISnapshotableDb CodeDb { get; private set; }
        public IColumnsDb<ReceiptsColumns> ReceiptsDb { get; private set; }
        public IDb BlocksDb { get; private set; }
        public IDb HeadersDb { get; private set; }
        public IDb BlockInfosDb { get; private set; }
        public IDb PendingTxsDb { get; private set; }
        public IDb ConfigsDb { get; private set; }
        public IDb EthRequestsDb { get; private set; }
        public IDb BloomDb { get; private set; }
        public IDb ChtDb { get; private set; }

        public void Dispose()
        {
            StateDb?.Dispose();
            CodeDb?.Dispose();
            ReceiptsDb?.Dispose();
            BlocksDb?.Dispose();
            HeadersDb?.Dispose();
            BlockInfosDb?.Dispose();
            PendingTxsDb?.Dispose();
            ConfigsDb?.Dispose();
            EthRequestsDb?.Dispose();
            BloomDb?.Dispose();
            ChtDb?.Dispose();
        }
    }
}