using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Databases
{
    public class ConsumerDepositApprovalsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "ConsumerDepositApprovals";

        public ConsumerDepositApprovalsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "consumerDepositApprovals", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => ConsumerMetrics.ConsumerDepositApprovalsDbReads++;
        protected override void UpdateWriteMetrics() => ConsumerMetrics.ConsumerDepositApprovalsDbWrites++;
    }
}