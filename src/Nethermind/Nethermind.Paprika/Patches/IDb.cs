// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Paprika.Crypto;
using Paprika.Store;

namespace Paprika;

/// <summary>
/// Provides the Paprika paged database operations used by Nethermind integrations.
/// </summary>
public interface IDb
{
    /// <summary>
    /// Starts a db transaction that is for the next block.
    /// </summary>
    /// <returns>The transaction that handles block operations.</returns>
    IBatch BeginNextBatch();

    /// <summary>
    /// Starts a readonly batch that preserves the latest database snapshot at the moment of creation.
    /// </summary>
    IReadOnlyBatch BeginReadOnlyBatch(string name = "");

    /// <summary>
    /// Starts a readonly batch for an exact state root. Throws when the root is outside retained history.
    /// </summary>
    IReadOnlyBatch BeginReadOnlyBatch(in Keccak stateHash, string name = "");

    /// <summary>
    /// Performs a flush using <see cref="IPageManager.Flush"/>.
    /// </summary>
    void Flush();

    /// <summary>
    /// Begins the readonly batch with the given Keccak or the latest.
    /// </summary>
    IReadOnlyBatch BeginReadOnlyBatchOrLatest(in Keccak stateHash, string name = "");

    /// <summary>
    /// Performs a snapshot of all the valid roots in the database.
    /// </summary>
    /// <returns>An array of roots.</returns>
    IReadOnlyBatch[] SnapshotAll();

    /// <summary>
    /// Whether there's a state with the given keccak.
    /// </summary>
    bool HasState(in Keccak keccak);

    /// <summary>
    /// Gets the history depth for the given db.
    /// </summary>
    int HistoryDepth { get; }

    /// <summary>
    /// Forces both memory-mapped view and file contents to disk for callers that need a stronger flush than <see cref="Flush"/>.
    /// </summary>
    void ForceFlush();
}
