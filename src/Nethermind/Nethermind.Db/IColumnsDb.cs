// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db
{
    public interface IColumnsDb<TKey> : IDbMeta
    {
        IDbWithSpan GetColumnDb(TKey key);
        IEnumerable<TKey> ColumnKeys { get; }
        public IReadOnlyColumnDb<TKey> CreateReadOnly(bool createInMemWriteStore) => new ReadOnlyColumnsDb<TKey>(this, createInMemWriteStore);
        IColumnsWriteBatch<TKey> StartWriteBatch();
    }

    public interface IColumnsWriteBatch<in TKey> : IDisposable
    {
        IWriteBatch GetColumnBatch(TKey key);
    }
}
