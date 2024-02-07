// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Db.ByPathState;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;

namespace Nethermind.Db
{
    public class StandardDbInitializer : RocksDbInitializer
    {
        private readonly IFileSystem _fileSystem;
        private readonly ILogManager _logManager;

        public StandardDbInitializer(
            IDbProvider? dbProvider,
            IDbFactory? rocksDbFactory,
            ILogManager logManager,
            IFileSystem? fileSystem = null)
            : base(dbProvider, rocksDbFactory)
        {
            _fileSystem = fileSystem ?? new FileSystem();
            _logManager = logManager;
        }

        public void InitStandardDbs(bool useReceiptsDb, bool useBlobsDb = true)
        {
            RegisterAll(useReceiptsDb, useBlobsDb);
            InitAll();
        }

        public async Task InitStandardDbsAsync(bool useReceiptsDb, bool useBlobsDb = true)
        {
            RegisterAll(useReceiptsDb, useBlobsDb);
            await InitAllAsync();
        }

        private void RegisterAll(bool useReceiptsDb, bool useBlobsDb)
        {
            RegisterDb(BuildDbSettings(DbNames.Blocks));
            RegisterDb(BuildDbSettings(DbNames.Headers));
            RegisterDb(BuildDbSettings(DbNames.BlockNumbers));
            RegisterDb(BuildDbSettings(DbNames.BlockInfos));
            RegisterDb(BuildDbSettings(DbNames.BadBlocks));

            DbSettings stateDbSettings = BuildDbSettings(DbNames.State);
            RegisterCustomDb(DbNames.State, () => new FullPruningDb(
                stateDbSettings,
                DbFactory is not MemDbFactory
                    ? new FullPruningInnerDbFactory(DbFactory, _fileSystem, stateDbSettings.DbPath)
                    : DbFactory,
                () => Interlocked.Increment(ref Metrics.StateDbInPruningWrites)));

            DbSettings pathStateDbSettings = BuildDbSettings(DbNames.PathState);
            RegisterCustomColumnDb(DbNames.PathState, () => new ByPathStateDb(
                pathStateDbSettings, DbFactory, _logManager));
            RegisterDb(BuildDbSettings(DbNames.Code));
            RegisterDb(BuildDbSettings(DbNames.Bloom));
            RegisterDb(BuildDbSettings(DbNames.CHT));
            RegisterDb(BuildDbSettings(DbNames.Witness));
            if (useReceiptsDb)
            {
                RegisterColumnsDb<ReceiptsColumns>(BuildDbSettings(DbNames.Receipts));
            }
            else
            {
                RegisterCustomColumnDb(DbNames.Receipts, () => new ReadOnlyColumnsDb<ReceiptsColumns>(new MemColumnsDb<ReceiptsColumns>(), false));
            }
            RegisterDb(BuildDbSettings(DbNames.Metadata));
            if (useBlobsDb)
            {
                RegisterColumnsDb<BlobTxsColumns>(BuildDbSettings(DbNames.BlobTransactions));
            }
        }

        private static DbSettings BuildDbSettings(string dbName, bool deleteOnStart = false)
        {
            return new(GetTitleDbName(dbName), dbName)
            {
                DeleteOnStart = deleteOnStart
            };
        }
    }
}
