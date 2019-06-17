using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db.Databases
{
    public class HeadersRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "Headers";

        public HeadersRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "headers", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => Metrics.HeaderDbReads++;
        protected override void UpdateWriteMetrics() => Metrics.HeaderDbWrites++;
    }
}