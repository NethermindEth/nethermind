// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db
{
    public static class DbProviderExtensions
    {
        public static ReadOnlyDbProvider AsReadOnly(this IDbProvider dbProvider, bool createInMemoryWriteStore)
        {
            return new(dbProvider, createInMemoryWriteStore);
        }
    }
}
