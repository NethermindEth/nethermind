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
using Nethermind.Baseline.Config;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;

namespace Nethermind.Baseline.Db
{
    public interface IBaselineDbProvider
    {
        public IDb? BaselineTreeDb { get;  }

        public IDb? BaselineTreeMetadataDb { get; }

        Task Init();
    }

    public class BaselineDbProvider : IBaselineDbProvider
    {
        private const string BaselineTreeDbName = "baselineTree";
        private const string BaselineTreeMetadataDbName = "baselineTreeMetadata";

        private readonly IDbFactory _dbFactory;
        private readonly IBaselineConfig _baselineConfig;
        private readonly IDbConfig _dbConfig;
        public BaselineDbProvider(
            IDbFactory dbFactory,
            IBaselineConfig baselineConfig,
            IDbConfig dbConfig)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _baselineConfig = baselineConfig ?? throw new ArgumentNullException(nameof(baselineConfig));
            _dbConfig = dbConfig ?? throw new ArgumentNullException(nameof(dbConfig));
        }
        public async Task Init()
        {
            var baselineDbConfig = new BaselineDbConfig(_baselineConfig, _dbConfig);
            HashSet<Task> allInitializers = new HashSet<Task>();
            allInitializers.Add(Task.Run(() => BaselineTreeDb = _dbFactory.Create(BaselineTreeDbName, baselineDbConfig)));
            allInitializers.Add(Task.Run(() => BaselineTreeMetadataDb = _dbFactory.Create(BaselineTreeMetadataDbName, baselineDbConfig)));
            await Task.WhenAll(allInitializers);
        }
        public IDb? BaselineTreeDb { get; private set; }

        public IDb? BaselineTreeMetadataDb { get; private set; }
    }
}
