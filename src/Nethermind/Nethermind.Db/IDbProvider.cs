// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;

namespace Nethermind.Db
{
    public interface IDbProvider : IDisposable
    {
        public IDb StateDb => GetDb<IDb>(DbNames.State);
        public IDb CodeDb => GetDb<IDb>(DbNames.Code);
        public IColumnsDb<ReceiptsColumns> ReceiptsDb => GetColumnDb<ReceiptsColumns>(DbNames.Receipts);
        public IDb BlocksDb => GetDb<IDb>(DbNames.Blocks);
        public IDb HeadersDb => GetDb<IDb>(DbNames.Headers);
        public IDb BlockNumbersDb => GetDb<IDb>(DbNames.BlockNumbers);
        public IDb BlockInfosDb => GetDb<IDb>(DbNames.BlockInfos);
        public IDb BadBlocksDb => GetDb<IDb>(DbNames.BadBlocks);

        // BloomDB progress / config (does not contain blooms - they are kept in bloom storage)
        public IDb BloomDb => GetDb<IDb>(DbNames.Bloom);

        public IDb MetadataDb => GetDb<IDb>(DbNames.Metadata);

        public IColumnsDb<BlobTxsColumns> BlobTransactionsDb => GetColumnDb<BlobTxsColumns>(DbNames.BlobTransactions);

        T GetDb<T>(string dbName) where T : class, IDb;
        IColumnsDb<T> GetColumnDb<T>(string dbName);

        void RegisterDb<T>(string dbName, T db) where T : class, IDb;
        void RegisterColumnDb<T>(string dbName, IColumnsDb<T> db);
        IEnumerable<KeyValuePair<string, IDbMeta>> GetAllDbMeta();

        void ConfigureServiceCollection(ContainerBuilder sc)
        {
            sc.AddSingleton(this);

            // TODO: Have hooks that automatically get these
            string[] dbNames = [
                DbNames.State,
                DbNames.Code,
                DbNames.Metadata,
                DbNames.Blocks,
                DbNames.Headers,
                DbNames.BlockInfos,
                DbNames.BadBlocks,
                DbNames.Bloom,
                DbNames.Metadata,
            ];
            foreach (string dbName in dbNames)
            {
                var db = GetDb<IDb>(dbName);
                sc.AddKeyedSingleton<IDb>(dbName, db);
                sc.AddKeyedSingleton<IDbMeta>(dbName, db);
                sc.AddKeyedSingleton<ITunableDb>(dbName, db as ITunableDb ?? new NoopTunableDb());
            }

            IColumnsDb<ReceiptsColumns> receiptColumnDb = GetColumnDb<ReceiptsColumns>(DbNames.Receipts);
            sc.AddSingleton<IColumnsDb<ReceiptsColumns>>(receiptColumnDb);
            if (receiptColumnDb is ITunableDb tunableDb2)
                sc.AddKeyedSingleton<ITunableDb>(DbNames.Receipts, tunableDb2);
            else
                sc.AddKeyedSingleton<ITunableDb>(DbNames.Receipts, new NoopTunableDb());
        }
    }
}
