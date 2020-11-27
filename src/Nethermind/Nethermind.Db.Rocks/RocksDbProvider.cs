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
using System.Threading.Tasks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks
{
    public class RocksDbProvider : IDbProvider
    {
        private readonly ILogManager _logManager;
        private readonly string _basePath;
        private readonly IDbConfig _defaultDbConfig;
        private readonly List<IDb> _otherDbs = new List<IDb>();

        public RocksDbProvider(ILogManager logManager, IDbConfig dbConfig, string basePath)
        {
            _logManager = logManager;
            _basePath = basePath;
            _defaultDbConfig = dbConfig;
        }

        public async Task Init(bool useReceiptsDb)
        {
            HashSet<Task> allInitializers = new HashSet<Task>();
            allInitializers.Add(Task.Run(() => BlocksDb = new BlocksRocksDb(_basePath, _defaultDbConfig, _logManager)));
            allInitializers.Add(Task.Run(() => HeadersDb = new HeadersRocksDb(_basePath, _defaultDbConfig, _logManager)));
            allInitializers.Add(Task.Run(() => BlockInfosDb = new BlockInfosRocksDb(_basePath, _defaultDbConfig, _logManager)));
            allInitializers.Add(Task.Run(() => StateDb = new StateDb(new StateRocksDb(_basePath, _defaultDbConfig, _logManager))));
            allInitializers.Add(Task.Run(() => CodeDb = new StateDb(new CodeRocksDb(_basePath, _defaultDbConfig, _logManager))));
            allInitializers.Add(Task.Run(() => PendingTxsDb = new PendingTxsRocksDb(_basePath, _defaultDbConfig, _logManager)));
            allInitializers.Add(Task.Run(() => BloomDb = new BloomRocksDb(_basePath, _defaultDbConfig, _logManager)));
            allInitializers.Add(Task.Run(() => ChtDb = new CanonicalHashRocksDb(_basePath, _defaultDbConfig, _logManager)));

            allInitializers.Add(Task.Run(() =>
            {
                if (useReceiptsDb)
                {
                    ReceiptsDb = new ReceiptsRocksDb(_basePath, _defaultDbConfig, _logManager);
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
        public IDb BeamStateDb { get; } = new MemDb();

        public IDb BaselineTreeDb { get; private set; }

        public IDb BaselineTreeMetadataDb { get; private set; }

        public IEnumerable<IDb> OtherDbs => _otherDbs;

        public DbModeHint DbMode => throw new NotImplementedException();

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
            BaselineTreeDb?.Dispose();
            BaselineTreeMetadataDb?.Dispose();

            if (_otherDbs != null)
            {
                foreach (var otherDb in _otherDbs)
                {
                    otherDb?.Dispose();
                }
            }
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
