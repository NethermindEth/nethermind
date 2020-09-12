using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.RocksDbExtractor.ProviderDecoders.RocksDb
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
