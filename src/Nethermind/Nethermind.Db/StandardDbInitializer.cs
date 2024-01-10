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
            IDbFactory? rocksDbFactory,
            IFileSystem? fileSystem = null)
            : base(dbProvider, rocksDbFactory)
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
            RegisterDb(BuildDbSettings(DbNames.Blocks, () => Metrics.BlocksDbReads++, () => Metrics.BlocksDbWrites++));
            RegisterDb(BuildDbSettings(DbNames.Headers, () => Metrics.HeaderDbReads++, () => Metrics.HeaderDbWrites++));
            RegisterDb(BuildDbSettings(DbNames.BlockNumbers, () => Metrics.BlockNumberDbReads++, () => Metrics.BlockNumberDbWrites++));
            RegisterDb(BuildDbSettings(DbNames.BlockInfos, () => Metrics.BlockInfosDbReads++, () => Metrics.BlockInfosDbWrites++));
            RegisterDb(BuildDbSettings(DbNames.BadBlocks, () => Metrics.BadBlocksDbReads++, () => Metrics.BadBlocksDbWrites++));

            DbSettings stateDbSettings = BuildDbSettings(DbNames.State, () => Metrics.StateDbReads++, () => Metrics.StateDbWrites++);
            RegisterCustomDb(DbNames.State, () => new FullPruningDb(
                stateDbSettings,
                DbFactory is not MemDbFactory
                    ? new FullPruningInnerDbFactory(DbFactory, _fileSystem, stateDbSettings.DbPath)
                    : DbFactory,
                () => Interlocked.Increment(ref Metrics.StateDbInPruningWrites)));

            RegisterDb(BuildDbSettings(DbNames.Code, () => Metrics.CodeDbReads++, () => Metrics.CodeDbWrites++));
            RegisterDb(BuildDbSettings(DbNames.Bloom, () => Metrics.BloomDbReads++, () => Metrics.BloomDbWrites++));
            RegisterDb(BuildDbSettings(DbNames.CHT, () => Metrics.CHTDbReads++, () => Metrics.CHTDbWrites++));
            RegisterDb(BuildDbSettings(DbNames.Witness, () => Metrics.WitnessDbReads++, () => Metrics.WitnessDbWrites++));
            if (useReceiptsDb)
            {
                RegisterColumnsDb<ReceiptsColumns>(BuildDbSettings(DbNames.Receipts, () => Metrics.ReceiptsDbReads++, () => Metrics.ReceiptsDbWrites++));
            }
            else
            {
                RegisterCustomColumnDb(DbNames.Receipts, () => new ReadOnlyColumnsDb<ReceiptsColumns>(new MemColumnsDb<ReceiptsColumns>(), false));
            }
            RegisterDb(BuildDbSettings(DbNames.Metadata, () => Metrics.MetadataDbReads++, () => Metrics.MetadataDbWrites++));
            if (useBlobsDb)
            {
                RegisterColumnsDb<BlobTxsColumns>(BuildDbSettings(DbNames.BlobTransactions, () => Metrics.BlobTransactionsDbReads++, () => Metrics.BlobTransactionsDbWrites++));
            }
        }

        private static DbSettings BuildDbSettings(string dbName, Action updateReadsMetrics, Action updateWriteMetrics, bool deleteOnStart = false)
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
