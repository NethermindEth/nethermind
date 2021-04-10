using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Tools.GasHistorian
{
    static class Program
    {
        static void Main(string[] args)
        {
            RocksDbSettings rocksDbSettings = new("BlockInfos", "BlockInfos");

            DbOnTheRocks dbOnTheRocks = new(
                "/Users/nethermind/db/",
                rocksDbSettings,
                DbConfig.Default,
                LimboLogs.Instance);

            dbOnTheRocks.Get(Rlp.Encode(1).Bytes);
        }
    }
}
