// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Autofac;
using Autofac.Core;
using Nethermind.Core;

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
            return _ctx.Resolve<IEnumerable<KeyValuePair<string, IDbMeta>>>();
        }
    }
}
