using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db.Databases
{
    public class PendingTxsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "PendingTxs";

        public PendingTxsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "pendingtxs", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => Metrics.PendingTxsDbReads++;
        protected override void UpdateWriteMetrics() => Metrics.PendingTxsDbWrites++;
    }
}