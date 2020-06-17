using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.RocksDbExtractor.rocksdb
{
    internal class PaymentClaimsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "PaymentClaims";

        public PaymentClaimsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager)
            : base(basePath, "paymentClaims", dbConfig, logManager)
        {
        }
    }
}