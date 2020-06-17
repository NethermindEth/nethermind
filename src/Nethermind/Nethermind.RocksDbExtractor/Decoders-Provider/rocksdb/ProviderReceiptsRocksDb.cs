using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.RocksDbExtractor.rocksdb
{
    internal class ProviderReceiptsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "ProviderReceipts";

        public ProviderReceiptsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager)
            : base(basePath, "providerReceipts", dbConfig, logManager)
        {
        }
    }
}