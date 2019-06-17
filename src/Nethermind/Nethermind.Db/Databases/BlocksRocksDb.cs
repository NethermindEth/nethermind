using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db.Databases
{
    public class BlocksRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "Blocks";

        public BlocksRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "blocks", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => Metrics.BlocksDbReads++;
        protected override void UpdateWriteMetrics() => Metrics.BlocksDbWrites++;
    }
}