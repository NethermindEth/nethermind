using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Databases
{
    public class ConsumerReceiptsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "ConsumerReceipts";

        public ConsumerReceiptsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "consumerReceipts", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => ConsumerMetrics.ConsumerReceiptsDbReads++;
        protected override void UpdateWriteMetrics() => ConsumerMetrics.ConsumerReceiptsDbWrites++;
    }
}