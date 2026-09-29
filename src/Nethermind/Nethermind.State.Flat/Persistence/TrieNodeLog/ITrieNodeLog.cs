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
    IView PinLiveGenerations();

    /// <param name="bypass">
    /// When true the log is drained first and the returned batch passes every column through unchanged; used for
    /// sync and import batches, whose range scans must see every node in RocksDB.
    /// </param>
    IWriteBatch StartWriteBatch(bool bypass);

    /// <summary>Merges every generation into RocksDB synchronously.</summary>
    void Drain();

    /// <summary>Discards every generation without merging it; pairs with a wipe of the trie columns.</summary>
    void Clear();

    public interface IView : IDisposable
    {
        void Bind(IReadOnlyKeyValueStore metadata);
        IReadOnlyKeyValueStore Wrap(FlatDbColumns column, IReadOnlyKeyValueStore inner);
    }

    public interface IWriteBatch : IDisposable
    {
        Core.IWriteBatch Wrap(FlatDbColumns column, Core.IWriteBatch inner);
        void Commit(IWriteOnlyKeyValueStore metadataBatch);
    }
}
