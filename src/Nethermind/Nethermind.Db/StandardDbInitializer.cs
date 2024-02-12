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
            RegisterDb(BuildDbSettings(DbNames.Blocks));
            RegisterDb(BuildDbSettings(DbNames.Headers));
            RegisterDb(BuildDbSettings(DbNames.BlockNumbers));
            RegisterDb(BuildDbSettings(DbNames.BlockInfos));
            RegisterDb(BuildDbSettings(DbNames.BadBlocks));
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
