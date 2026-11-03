// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// The store whose commits carry the flat DB's markers: the state pointer, the wipe marker, the format markers and the
/// trie node log's confirmed versions.
/// </summary>
/// <remarks>
/// The trie node log, the activation policy, the patricia drop and the history writer read the markers here rather than
/// off the RocksDB metadata column, so the store that confirms the log is chosen once, by the layout. The trie node
/// log's merged markers, second-level generation numbers and shard manifest stay in the RocksDB metadata column.
/// </remarks>
public interface IFlatMetadataStore
{
    /// <summary>
    /// The markers as of the latest commit: the state pointer, the wipe marker, the format markers and the trie node
    /// log's confirmed versions.
    /// </summary>
    /// <remarks>
    /// Each get may return a copy of one commit or the live markers, so markers that must agree are read through one
    /// instance: a store that returns copies then serves them from the same commit.
    /// </remarks>
    IReadOnlyKeyValueStore Committed { get; }

    /// <summary>Opens the markers a reader of <paramref name="rocksDbSnapshot"/> binds to.</summary>
    /// <remarks>
    /// Called by <see cref="TrieNodeLog.ITrieNodeLog.OpenView"/> once it has taken the RocksDB snapshot, never before: a
    /// merge the snapshot already shows relied only on versions committed before it, so markers opened afterwards
    /// confirm every one of them. The view owns the result and disposes it.
    /// </remarks>
    ISnapshot OpenSnapshot(IColumnDbSnapshot<FlatDbColumns> rocksDbSnapshot);

    /// <summary>
    /// Durably wipes the flat state and its pointer, keeping the format markers, and marks the DB wiped for a state sync.
    /// </summary>
    void WipeForResync();

    /// <summary>The markers one reader is bound to.</summary>
    public interface ISnapshot : IDisposable
    {
        /// <summary>The markers as of the commit the reader is bound to.</summary>
        IReadOnlyKeyValueStore Metadata { get; }
    }
}
