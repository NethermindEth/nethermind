using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db.Databases
{
    public class ReceiptsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "Receipts";

        public ReceiptsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "receipts", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => Metrics.ReceiptsDbReads++;
        protected override void UpdateWriteMetrics() => Metrics.ReceiptsDbWrites++;
    }
}