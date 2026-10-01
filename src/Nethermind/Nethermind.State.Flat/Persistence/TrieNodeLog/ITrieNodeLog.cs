// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// Append-only log with an in-memory index that sits in front of the flat DB trie columns inside
/// <see cref="RocksDbPersistence"/>: node writes for the covered columns go to the log and are merged into
/// RocksDB one generation at a time, so a node rewritten several times within a generation reaches RocksDB once.
/// </summary>
/// <remarks>
/// A reader goes through <see cref="OpenView"/>, which creates the RocksDB snapshot itself so the generations it
/// may need are pinned before the snapshot exists and the view serves exactly the log version the snapshot's
/// metadata confirms. Write batches are committed by <see cref="IWriteBatch.Commit"/> before the RocksDB batch is
/// written and disposed after it.
/// </remarks>
public interface ITrieNodeLog
{
    /// <summary>
    /// Opens a reader's view: a snapshot of <paramref name="db"/> together with the log generations whose records that
    /// snapshot does not yet contain, pinned so a merge cannot delete them while the view is open.
    /// </summary>
    IView OpenView(IColumnsDb<FlatDbColumns> db, ReaderFlags flags);

    /// <summary>Starts the log side of a persistence write batch; one log-backed batch may be open at a time.</summary>
    /// <param name="bypass">
    /// When true the log is drained first and the returned batch passes every column through unchanged; used for
    /// sync and import batches, whose range scans must see every node in RocksDB.
    /// </param>
    IWriteBatch StartWriteBatch(bool bypass);

    /// <summary>Merges every generation into RocksDB synchronously.</summary>
    void Drain();

    /// <summary>Discards every generation without merging it; pairs with a wipe of the trie columns.</summary>
    void Clear();

    /// <summary>A RocksDB snapshot and the log as seen at its version.</summary>
    public interface IView : IDisposable
    {
        IColumnDbSnapshot<FlatDbColumns> Snapshot { get; }

        /// <summary>The snapshot's column, with the log consulted first for a column the log covers.</summary>
        IReadOnlyKeyValueStore GetColumn(FlatDbColumns column);
    }

    /// <summary>The log side of one persistence write batch.</summary>
    public interface IWriteBatch : IDisposable
    {
        /// <summary>Wraps a column batch so its writes go to the log; returns <paramref name="inner"/> for a column the log does not cover.</summary>
        Core.IWriteBatch Wrap(FlatDbColumns column, Core.IWriteBatch inner);

        /// <summary>
        /// Makes the batch's records durable and visible, and puts the log version into <paramref name="metadataBatch"/>
        /// so RocksDB confirms it with the state pointer. Called before the RocksDB batch is written; the batch is
        /// disposed after it.
        /// </summary>
        void Commit(IWriteOnlyKeyValueStore metadataBatch);
    }
}
