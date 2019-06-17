using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db.Databases
{
    public class ConfigsRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "Configs";

        public ConfigsRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "configs", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => Metrics.ConfigsDbReads++;
        protected override void UpdateWriteMetrics() => Metrics.CodeDbWrites++;
    }
}