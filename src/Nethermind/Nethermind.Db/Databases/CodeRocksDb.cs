using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db.Databases
{
    public class CodeRocksDb : DbOnTheRocks
    {
        public override string Name { get; } = "Code";

        public CodeRocksDb(string basePath, IDbConfig dbConfig, ILogManager logManager = null)
            : base(basePath, "code", dbConfig, logManager)
        {
        }

        protected override void UpdateReadMetrics() => Metrics.CodeDbReads++;
        protected override void UpdateWriteMetrics() => Metrics.CodeDbWrites++;
    }
}