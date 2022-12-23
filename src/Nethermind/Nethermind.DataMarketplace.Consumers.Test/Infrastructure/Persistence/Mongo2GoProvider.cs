// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Mongo2Go;
using MongoDB.Driver;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence
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
