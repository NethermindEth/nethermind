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
using Mongo2Go;
using MongoDB.Driver;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Tests.Persistence.Mongo
{
    public class Mongo2GoProvider : IMongoProvider, IDisposable
    {
        private MongoProvider _mongoProvider;
        private MongoDbRunner _runner;

        public Mongo2GoProvider(INdmMongoConfig config, ILogManager logManager)
        {
            _runner = MongoDbRunner.Start();
            config.ConnectionString = _runner.ConnectionString;
            _mongoProvider = new MongoProvider(config, logManager);
        }

        public IMongoDatabase GetDatabase()
        {
            return _mongoProvider.GetDatabase();
        }

        public void Dispose()
        {
            _runner?.Dispose();
        }
    }
}