using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db.Databases
{
    public class StateRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "State";

        public StateRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "state", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => Metrics.StateDbReads++;
        protected override void UpdateWriteMetrics() => Metrics.StateDbWrites++;
    }
}