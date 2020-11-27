using System;
using Nethermind.Api;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks
{
    public class RocksDbFactory : IRocksDbFactory
    {
        private readonly IDbConfig _dbConfig;
        private readonly ILogManager _logManager;
        private readonly string _basePath;
        public RocksDbFactory()
        {

        }
        public IDb CreateDb(RocksDbSettings rocksDbSpecification)
        { 
            return new SimpleRocksDb(_basePath, rocksDbSpecification.DbPath, rocksDbSpecification.DbName, _dbConfig, _logManager);
        }

        public ISnapshotableDb CreateSnapshotableDb(RocksDbSettings rocksDbSettings)
        {
            return new StateDb(CreateDb(rocksDbSettings));
        }
    }
}
