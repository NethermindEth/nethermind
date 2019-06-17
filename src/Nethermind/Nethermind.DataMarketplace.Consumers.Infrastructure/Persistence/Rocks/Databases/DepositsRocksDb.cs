using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Databases
{
    public class DepositsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "Deposits";

        public DepositsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "deposits", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => ConsumerMetrics.DepositsDbReads++;
        protected override void UpdateWriteMetrics() => ConsumerMetrics.DepositsDbWrites++;
    }
}