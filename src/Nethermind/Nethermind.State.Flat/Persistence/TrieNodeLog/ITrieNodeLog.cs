// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// Append-only log with an in-memory index that sits in front of the flat DB trie columns inside
/// <see cref="RocksDbPersistence"/>: node writes for the covered columns go to the log and are merged into
/// RocksDB one generation at a time, so a node rewritten several times within a generation reaches RocksDB once.
/// </summary>
/// <remarks>
/// Reader creation is a three-step handshake so the log view is consistent with the RocksDB snapshot without a
/// lock spanning both: <see cref="PinLiveGenerations"/>, then create the RocksDB snapshot, then
/// <see cref="IView.Bind"/> with that snapshot's metadata column. Write batches are committed by
/// <see cref="IWriteBatch.Commit"/> before the RocksDB batch is written and disposed after it.
/// </remarks>
public interface ITrieNodeLog
{
    /// <summary>
    /// First step of creating a reader: leases every generation currently in memory, before the caller creates its
    /// RocksDB snapshot, so a generation merged and deleted in between stays readable. The returned view is
    /// completed with <see cref="IView.Bind"/> once the snapshot exists.
    /// </summary>
    IView PinLiveGenerations();

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

    /// <summary>The log as seen by one reader: the generations it pinned, at the version its RocksDB snapshot confirms.</summary>
    public interface IView : IDisposable
    {
        /// <summary>
        /// Completes the view from the RocksDB snapshot's metadata column: reads the log version and the merged-generation
        /// marker the snapshot confirms, pins any generation started since <see cref="PinLiveGenerations"/>, and
        /// releases the pins on generations the snapshot already contains.
        /// </summary>
        void Bind(IReadOnlyKeyValueStore metadata);

        /// <summary>Wraps a column of the snapshot so reads consult the log before it; returns <paramref name="inner"/> for a column the log does not cover.</summary>
        IReadOnlyKeyValueStore Wrap(FlatDbColumns column, IReadOnlyKeyValueStore inner);
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
