using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.RocksDbExtractor.rocksdb
{
    internal class ConsumersRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "Consumers";

        public ConsumersRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager)
            : base(basePath, "consumers", dbConfig, logManager)
        {
        }
    }
}