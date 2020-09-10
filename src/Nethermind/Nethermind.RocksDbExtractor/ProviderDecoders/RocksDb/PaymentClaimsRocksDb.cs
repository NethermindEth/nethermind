using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.RocksDbExtractor.ProviderDecoders.RocksDb
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
