using System;
using Nethermind.Api;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks
{
    public class RocksDbFactory : IRocksDbFactory
    {
        private readonly IInitConfig _initConfig;
        private readonly IDbConfig _dbConfig;
        private readonly ILogManager _logManager;
        public RocksDbFactory()
        {

        }
        public IDb CreateDb(RocksDbSettings rocksDbSpecification)
        { 
            return new SimpleRocksDb(_initConfig.BaseDbPath, rocksDbSpecification.DbPath, rocksDbSpecification.DbName, _dbConfig, _logManager);
        }

        public ISnapshotableDb CreateSnapshotableDb(RocksDbSettings rocksDbSettings)
        {
            throw new NotImplementedException();
        }
    }
}
