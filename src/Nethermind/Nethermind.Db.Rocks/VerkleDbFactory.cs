using System;
using System.IO;
using System.IO.Abstractions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks
{
    public enum DbMode
    {
        [ConfigItem(Description = "Diagnostics mode which uses an in-memory DB")]
        MemDb,
        [ConfigItem(Description = "Diagnostics mode which uses an Persistant DB")]
        PersistantDb,
        [ConfigItem(Description = "Diagnostics mode which uses a read-only DB")]
        ReadOnlyDb
    }

    public class VerkleDbFactory
    {
        private static (IDbProvider DbProvider, IDbFactory dbFactory) InitDbApi(DbMode diagnosticMode, string baseDbPath, bool storeReceipts)
        {
            DbConfig dbConfig = new DbConfig();
            DisposableStack disposeStack = new DisposableStack();
            IDbProvider dbProvider;
            IDbFactory dbFactory;
            switch (diagnosticMode)
            {
                case DbMode.ReadOnlyDb:
                    DbProvider rocksDbProvider = new DbProvider();
                    dbProvider = new ReadOnlyDbProvider(rocksDbProvider, storeReceipts); // ToDo storeReceipts as createInMemoryWriteStore - bug?
                    disposeStack.Push(rocksDbProvider);
                    dbFactory = new RocksDbFactory(dbConfig, NullLogManager.Instance, Path.Combine(baseDbPath, "debug"));
                    break;
                case DbMode.MemDb:
                    dbProvider = new DbProvider();
                    dbFactory = new MemDbFactory();
                    break;
                case DbMode.PersistantDb:
                    dbProvider = new DbProvider();
                    dbFactory = new RocksDbFactory(dbConfig, NullLogManager.Instance, baseDbPath);
                    break;
                default:
                    throw new ArgumentException();

            }

            return (dbProvider, dbFactory);
        }

        public static IDbProvider InitDatabase(DbMode dbMode, string? dbPath)
        {
            (IDbProvider dbProvider, IDbFactory dbFactory) = InitDbApi(dbMode, dbPath ?? "testDb", true);
            var dbInitializer = new StandardDbInitializer(dbProvider, dbFactory, new FileSystem());
            dbInitializer.InitStandardDbs(true);
            return dbProvider;
        }
    }
}
