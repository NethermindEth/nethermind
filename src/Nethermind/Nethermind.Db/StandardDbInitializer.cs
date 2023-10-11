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
        private readonly bool _fullPruning;

        public StandardDbInitializer(
            IDbProvider? dbProvider,
            IRocksDbFactory? rocksDbFactory,
            IMemDbFactory? memDbFactory,
            IFileSystem? fileSystem = null,
            bool fullPruning = false)
            : base(dbProvider, rocksDbFactory, memDbFactory)
        {
            _fileSystem = fileSystem ?? new FileSystem();
            _fullPruning = fullPruning;
        }

        public void InitStandardDbs(bool useReceiptsDb)
        {
            RegisterAll(useReceiptsDb);
            InitAll();
        }

        public async Task InitStandardDbsAsync(bool useReceiptsDb)
        {
            RegisterAll(useReceiptsDb);
            await InitAllAsync();
        }

        private void RegisterAll(bool useReceiptsDb)
        {
            RegisterDb(BuildRocksDbSettings(DbNames.Blocks, () => Metrics.BlocksDbReads++, () => Metrics.BlocksDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.Headers, () => Metrics.HeaderDbReads++, () => Metrics.HeaderDbWrites++));
            RegisterDb(BuildRocksDbSettings(DbNames.BlockNumbers, () => Metrics.BlockNumberDbReads++, () => Metrics.BlockNumberDbWrites++));
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
