// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Db.FullPruning;

namespace Nethermind.Db
{
    public class StandardDbInitializer : RocksDbInitializer
    {
        private readonly IFileSystem _fileSystem;

        public StandardDbInitializer(
            IDbProvider? dbProvider,
            IRocksDbFactory? rocksDbFactory,
            IMemDbFactory? memDbFactory,
            IFileSystem? fileSystem = null)
            : base(dbProvider, rocksDbFactory, memDbFactory)
        {
            _fileSystem = fileSystem ?? new FileSystem();
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
            RegisterDb(BuildRocksDbSettings(DbNames.Blocks, () => Metrics.BlocksDbReads++, () => Metrics.BlocksDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.Headers, () => Metrics.HeaderDbReads++, () => Metrics.HeaderDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.BlockInfos, () => Metrics.BlockInfosDbReads++, () => Metrics.BlockInfosDbWrites++));

            RocksDbSettings stateDbSettings = BuildRocksDbSettings(DbNames.State, () => Metrics.StateDbReads++, () => Metrics.StateDbWrites++);
            RegisterCustomDb(DbNames.State, () => new FullPruningDb(
                stateDbSettings,
                PersistedDb
                    ? new FullPruningInnerDbFactory(RocksDbFactory, _fileSystem, stateDbSettings.DbPath)
                    : new MemDbFactoryToRocksDbAdapter(MemDbFactory),
                () => Interlocked.Increment(ref Metrics.StateDbInPruningWrites)));

            RegisterDb(BuildRocksDbSettings(DbNames.Code, () => Metrics.CodeDbReads++, () => Metrics.CodeDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.Bloom, () => Metrics.BloomDbReads++, () => Metrics.BloomDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.CHT, () => Metrics.CHTDbReads++, () => Metrics.CHTDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.Witness, () => Metrics.WitnessDbReads++, () => Metrics.WitnessDbWrites++));
            if (useReceiptsDb)
            {
                RegisterColumnsDb<ReceiptsColumns>(BuildRocksDbSettings(DbNames.Receipts, () => Metrics.ReceiptsDbReads++, () => Metrics.ReceiptsDbWrites++));
            }
            else
            {
                RegisterCustomDb(DbNames.Receipts, () => new ReadOnlyColumnsDb<ReceiptsColumns>(new MemColumnsDb<ReceiptsColumns>(), false));
            }
            RegisterDb(BuildRocksDbSettings(DbNames.Metadata, () => Metrics.MetadataDbReads++, () => Metrics.MetadataDbWrites++));
            if (useBlobsDb)
            {
                RegisterColumnsDb<BlobTxsColumns>(BuildRocksDbSettings(DbNames.BlobTransactions, () => Metrics.BlobTransactionsDbReads++, () => Metrics.BlobTransactionsDbWrites++));
            }
        }

        private RocksDbSettings BuildRocksDbSettings(string dbName, Action updateReadsMetrics, Action updateWriteMetrics, bool deleteOnStart = false)
        {
            return new(GetTitleDbName(dbName), dbName)
            {
                UpdateReadMetrics = updateReadsMetrics,
                UpdateWriteMetrics = updateWriteMetrics,
                DeleteOnStart = deleteOnStart
            };
        }
    }
}
