using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.RocksDbExtractor.rocksdb
{
    internal class ProviderSessionsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "ProviderSessions";

        public ProviderSessionsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager)
            : base(basePath, "providerSessions", dbConfig, logManager)
        {
        }
    }
}