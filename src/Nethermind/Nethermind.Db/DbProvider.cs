// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Autofac;

namespace Nethermind.Db
{
    public class DbProvider : IDbProvider
    {
        private IComponentContext _ctx;

        public DbProvider(IComponentContext ctx)
        {
            _ctx = ctx;
        }
        public IColumnsDb<ReceiptsColumns> ReceiptsDb => GetColumnDb<ReceiptsColumns>(DbNames.Receipts);

        public IColumnsDb<BlobTxsColumns> BlobTransactionsDb => GetColumnDb<BlobTxsColumns>(DbNames.BlobTransactions);

        public T GetDb<T>(string dbName) where T : class, IDb
        {
            return _ctx.ResolveNamed<T>(dbName);
        }

        public IColumnsDb<T> GetColumnDb<T>(string dbName)
        {
            return _ctx.ResolveNamed<IColumnsDb<T>>(dbName);
        }

        public IEnumerable<KeyValuePair<string, IDbMeta>> GetAllDbMeta()
        {
            return _ctx.Resolve<DbRegistry>().GetAllDbMeta();
        }
    }

    public class DbRegistry
    {
        private IDictionary<string, IDbMeta> _dbMetas = new Dictionary<string, IDbMeta>();

        public void RegisterDb(string name, IDbMeta meta)
        {
            _dbMetas[name] = meta;
        }

        public IEnumerable<KeyValuePair<string, IDbMeta>> GetAllDbMeta()
        {
            return _dbMetas;
        }
    }
}
