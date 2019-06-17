using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db.Databases
{
    public class BlockInfosRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "BlockInfos";

        public BlockInfosRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "blockInfos", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => Metrics.BlockInfosDbReads++;
        protected override void UpdateWriteMetrics() => Metrics.BlockInfosDbWrites++;
    }
}