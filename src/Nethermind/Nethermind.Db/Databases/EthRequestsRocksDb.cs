using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db.Databases
{
    public class EthRequestsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "EthRequests";

        public EthRequestsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "ethRequests", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => Metrics.EthRequestsDbReads++;
        protected override void UpdateWriteMetrics() => Metrics.EthRequestsDbWrites++;
    }
}