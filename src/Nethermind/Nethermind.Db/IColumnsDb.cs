// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db
{
    public interface IColumnsDb<TKey> : IDbMeta, IDisposable
    {
        IDb GetColumnDb(TKey key);
        IEnumerable<TKey> ColumnKeys { get; }
        public IReadOnlyColumnDb<TKey> CreateReadOnly(bool createInMemWriteStore) => new ReadOnlyColumnsDb<TKey>(this, createInMemWriteStore);
        IColumnsWriteBatch<TKey> StartWriteBatch();
        IColumnDbSnapshot<TKey> CreateSnapshot();

        /// <summary>
        /// Creates a snapshot whose readers may be tuned for sequential full scans.
        /// </summary>
        /// <param name="sequentialReadAhead">
        /// Hint that the snapshot will serve <see cref="ReadFlags.HintReadAhead"/> reads over path-ordered keys
        /// as part of a sequential full scan. Implementations may ignore it.
        /// </param>
        IColumnDbSnapshot<TKey> CreateSnapshot(bool sequentialReadAhead) => CreateSnapshot();
    }

    public interface IColumnsWriteBatch<in TKey> : IDisposable
    {
        IWriteBatch GetColumnBatch(TKey key);
        void Clear();
    }


    public interface IColumnDbSnapshot<in TKey> : IDisposable
    {
        IReadOnlyKeyValueStore GetColumn(TKey key);
    }
}
