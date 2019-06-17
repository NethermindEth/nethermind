using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Databases;
using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks
{
    public class ConsumerRocksDbProvider : IConsumerDbProvider
    {
        public IDb ConsumerDepositApprovalsDb { get; }
        public IDb ConsumerReceiptsDb { get; }
        public IDb ConsumerSessionsDb { get; }
        public IDb DepositsDb { get; }

        public ConsumerRocksDbProvider(string basePath, IDbConfig dbConfig, ILogManager logManager)
        {
            ConsumerDepositApprovalsDb = new ConsumerDepositApprovalsRocksDb(basePath, dbConfig, logManager);
            ConsumerReceiptsDb = new ConsumerReceiptsRocksDb(basePath, dbConfig, logManager);
            ConsumerSessionsDb = new ConsumerSessionsRocksDb(basePath, dbConfig, logManager);
            DepositsDb = new DepositsRocksDb(basePath, dbConfig, logManager);
        }

        public void Dispose()
        {
            ConsumerDepositApprovalsDb?.Dispose();
            ConsumerReceiptsDb?.Dispose();
            ConsumerSessionsDb?.Dispose();
            DepositsDb?.Dispose();
        }
    }
}