// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;

namespace Nethermind.Db
{
    public interface IColumnsDb<TKey> : IDbMeta, IDisposable where TKey : notnull
    {
        IDb GetColumnDb(TKey key);
        IEnumerable<TKey> ColumnKeys { get; }
        public IReadOnlyColumnDb<TKey> CreateReadOnly(bool createInMemWriteStore) => new ReadOnlyColumnsDb<TKey>(this, createInMemWriteStore);
        IColumnsWriteBatch<TKey> StartWriteBatch();
        /// <summary>Creates an abortable batch with atomic publication across columns.</summary>
        /// <exception cref="NotSupportedException">The database does not support atomic publication.</exception>
        IAtomicColumnsWriteBatch<TKey> StartAtomicWriteBatch() => ThrowAtomicWritesNotSupported();
        IColumnDbSnapshot<TKey> CreateSnapshot();

        /// <summary>
        /// Creates a snapshot whose readers may be tuned for sequential full scans.
        /// </summary>
        /// <param name="sequentialReadAhead">
        /// Hint that the snapshot will serve <see cref="ReadFlags.HintReadAhead"/> reads over path-ordered keys
        /// as part of a sequential full scan. Implementations may ignore it.
        /// </param>
        IColumnDbSnapshot<TKey> CreateSnapshot(bool sequentialReadAhead) => CreateSnapshot();

        [DoesNotReturn, StackTraceHidden]
        private static IAtomicColumnsWriteBatch<TKey> ThrowAtomicWritesNotSupported() =>
            throw new NotSupportedException("Atomic column write batches are not supported by this database.");
    }

    public interface IColumnsWriteBatch<in TKey> : IDisposable where TKey : notnull
    {
        IWriteBatch GetColumnBatch(TKey key);
        void Clear();
    }

    /// <summary>A column batch whose disposal discards writes unless explicitly committed.</summary>
    public interface IAtomicColumnsWriteBatch<in TKey> : IColumnsWriteBatch<TKey> where TKey : notnull
    {
        /// <summary>Atomically publishes pending writes across all columns.</summary>
        void Commit();
    }


    public interface IColumnDbSnapshot<in TKey> : IDisposable where TKey : notnull
    {
        IReadOnlyKeyValueStore GetColumn(TKey key);
    }
}
