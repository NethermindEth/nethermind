using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db.Databases
{
    public class TraceRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "Trace";

        public TraceRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "trace", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => Metrics.TraceDbReads++;
        protected override void UpdateWriteMetrics() => Metrics.TraceDbWrites++;
    }
}