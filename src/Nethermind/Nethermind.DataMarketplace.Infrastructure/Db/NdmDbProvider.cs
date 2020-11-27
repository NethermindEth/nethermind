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
// 

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Infrastructure.Db
{
    public interface INdmDbProvider
    {
        public IDb? ConfigsDb { get; }

        public IDb? EthRequestsDb { get; }

        Task Init();
    }

    public class NdmDbProvider : INdmDbProvider
    {
        private const string ConfigsDbName = "Configs";
        private const string EthRequestsDbName = "EthRequests";
        private const string ConfigsDbPath = "configs";
        private const string EthRequestsDbPath = "ethRequests";

        private readonly IDbProvider _dbProvider;
        private readonly ILogManager _logManager;
        private readonly IRocksDbFactory _rocksDbFactory;
        private readonly IMemDbFactory _memDbFactory;
        public NdmDbProvider(
            IDbProvider dbProvider,
            ILogManager logManager,
            IRocksDbFactory rocksDbFactory,
            IMemDbFactory memDbFactory)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _rocksDbFactory = rocksDbFactory ?? throw new ArgumentNullException(nameof(rocksDbFactory));
            _memDbFactory = memDbFactory ?? throw new ArgumentNullException(nameof(memDbFactory));
        }
        public async Task Init()
        {
            HashSet<Task> allInitializers = new HashSet<Task>();
            allInitializers.Add(Task.Run(() =>
            {
                if (_dbProvider.DbMode == DbModeHint.Persisted)
                    ConfigsDb = _rocksDbFactory.CreateDb(new RocksDbSettings()
                    {
                        DbName = ConfigsDbName,
                        DbPath = ConfigsDbPath
                    });
                else
                    ConfigsDb = _memDbFactory.CreateDb(ConfigsDbName);
                _dbProvider.RegisterDb(ConfigsDbName, ConfigsDb);
            }));
            allInitializers.Add(Task.Run(() =>
            {
                if (_dbProvider.DbMode == DbModeHint.Persisted)
                    EthRequestsDb = _rocksDbFactory.CreateDb(new RocksDbSettings()
                    {
                        DbName = EthRequestsDbName,
                        DbPath = EthRequestsDbPath,
                        UpdateReadMetrics = () => Metrics.EthRequestsDbReads++,
                        UpdateWriteMetrics = () => Metrics.EthRequestsDbWrites++,
                    });
                else
                    EthRequestsDb = _memDbFactory.CreateDb(EthRequestsDbName);
                _dbProvider.RegisterDb(EthRequestsDbName, EthRequestsDb);
            }));
            await Task.WhenAll(allInitializers);
        }
        public IDb? ConfigsDb { get; private set; }

        public IDb? EthRequestsDb { get; private set; }
    }
}
