using System.Threading.Tasks;

namespace Nethermind.Db
{
    public class StandardDbInitializer
    {
        private readonly IRocksDbFactory _rocksDbFactory;
        private readonly IMemDbFactory _memDbFactory;
        private readonly IDbProvider _dbProvider;
        public async Task InitStandardDbs()
        {
            // ToDo Task run, metrics, implement constructor
            _dbProvider.RegisterDb("Code", CreateDb(new RocksDbSettings()
            {
                DbName = "Code",
                DbPath = "code",
            }, "Code"));
        }

        private IDb CreateDb(RocksDbSettings rocksDbSettings, string dbName)
        {
            if (_dbProvider.DbMode == DbModeHint.Persisted)
            {
                return _rocksDbFactory.CreateDb(rocksDbSettings);
            }

            return _memDbFactory.CreateDb(dbName);
        }
    }
}
