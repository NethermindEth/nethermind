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
using Nethermind.Db.Rocks;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Infrastructure.Db
{
    public class NdmDbProvider
    {
        private readonly IDbProvider _dbProvider;
        private readonly ILogManager _logManager;
        public NdmDbProvider(
            IDbProvider dbProvider,
            ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }
        public async Task Init()
        {
            HashSet<Task> allInitializers = new HashSet<Task>();
            allInitializers.Add(Task.Run(() => ConfigsDb = _dbProvider.RegisterDb((basePath, config) => new ConfigsRocksDb(basePath, config, _logManager))));
            allInitializers.Add(Task.Run(() => EthRequestsDb = _dbProvider.RegisterDb((basePath, config) => new EthRequestsRocksDb(basePath, config, _logManager))));
            await Task.WhenAll(allInitializers);
        }
        public IDb? ConfigsDb { get; private set; }

        public IDb? EthRequestsDb { get; private set; }
    }
}
