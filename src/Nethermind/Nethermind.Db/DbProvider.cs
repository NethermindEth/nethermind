// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;

namespace Nethermind.Db
{
    public class AutofacDbProvider(IComponentContext ctx) : IDbProvider
    {
        public virtual void Dispose()
        {
        }

        public T GetDb<T>(string dbName) where T : class, IDb
        {
            return (T)ctx.ResolveKeyed<IDb>(dbName);
        }

        public IColumnsDb<T> GetColumnDb<T>(string dbName)
        {
            return ctx.Resolve<IColumnsDb<T>>();
        }
    }
}
