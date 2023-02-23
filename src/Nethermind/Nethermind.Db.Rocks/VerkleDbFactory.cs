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
        private static (IDbProvider DbProvider, RocksDbFactory RocksDbFactory, MemDbFactory MemDbFactory) InitDbApi(DbMode diagnosticMode, string baseDbPath, bool storeReceipts)
        {
            DbConfig dbConfig = new DbConfig();
            DisposableStack disposeStack = new DisposableStack();
            IDbProvider dbProvider;
            RocksDbFactory rocksDbFactory;
            MemDbFactory memDbFactory;
            switch (diagnosticMode)
            {
                case DbMode.ReadOnlyDb:
                    DbProvider rocksDbProvider = new DbProvider(DbModeHint.Persisted);
                    dbProvider = new ReadOnlyDbProvider(rocksDbProvider, storeReceipts); // ToDo storeReceipts as createInMemoryWriteStore - bug?
                    disposeStack.Push(rocksDbProvider);
                    rocksDbFactory = new RocksDbFactory(dbConfig, NullLogManager.Instance, Path.Combine(baseDbPath, "debug"));
                    memDbFactory = new MemDbFactory();
                    break;
                case DbMode.MemDb:
                    dbProvider = new DbProvider(DbModeHint.Mem);
                    rocksDbFactory = new RocksDbFactory(dbConfig, NullLogManager.Instance, Path.Combine(baseDbPath, "debug"));
                    memDbFactory = new MemDbFactory();
                    break;
                case DbMode.PersistantDb:
                    dbProvider = new DbProvider(DbModeHint.Persisted);
                    rocksDbFactory = new RocksDbFactory(dbConfig, NullLogManager.Instance, baseDbPath);
                    memDbFactory = new MemDbFactory();
                    break;
                default:
                    throw new ArgumentException();

            }

            return (dbProvider, rocksDbFactory, memDbFactory);
        }

        public static IDbProvider InitDatabase(DbMode dbMode, string? dbPath)
        {
            (IDbProvider dbProvider, RocksDbFactory rocksDbFactory, MemDbFactory memDbFactory) = InitDbApi(dbMode, dbPath ?? "testDb", true);
            StandardDbInitializer dbInitializer = new StandardDbInitializer(dbProvider, rocksDbFactory, memDbFactory, new FileSystem());
            dbInitializer.InitStandardDbs(true);
            return dbProvider;
        }
    }
}
