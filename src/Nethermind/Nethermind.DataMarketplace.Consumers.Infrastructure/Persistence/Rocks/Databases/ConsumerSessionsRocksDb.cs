using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Databases
{
    public class ConsumerSessionsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "ConsumerSessions";

        public ConsumerSessionsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "consumerSessions", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => ConsumerMetrics.ConsumerSessionsDbReads++;
        protected override void UpdateWriteMetrics() => ConsumerMetrics.ConsumerSessionsDbWrites++;
    }
}