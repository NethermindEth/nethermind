// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Receives notifications about trie nodes written to, or re-referenced in, the underlying
/// <see cref="INodeStorage"/> as block commit sets are persisted by <see cref="TrieStore"/>.
/// </summary>
/// <remarks>
/// Used by the partial archive mode to maintain a per-path latest-version index and an expiry
/// journal of superseded node keys, enabling a rolling window of historical state on disk.
/// Thread-safety: <see cref="OnNodePersisted"/> is invoked concurrently from parallel
/// persistence tasks; <see cref="OnNodeRecommitted"/> is invoked on the block-processing path
/// and must be non-blocking; <see cref="OnSnapshotPersisted"/> is invoked on the pruning thread
/// after a persistence pass completes and acts as a barrier — implementations should durably
/// apply all previously reported events before returning.
/// </remarks>
public interface IPersistedNodeObserver
{
    /// <summary>
    /// Reports a node written to node storage while persisting the commit set of
    /// <paramref name="blockNumber"/>. Only called for nodes with a non-null keccak
    /// (nodes inlined in their parent RLP are not stored separately).
    /// </summary>
    void OnNodePersisted(Hash256? address, in TreePath path, Hash256 keccak, ulong blockNumber);

    /// <summary>
    /// Reports a committed node that deduplicated against an existing dirty-cache record,
    /// i.e. the exact same node (address, path, keccak) is live again at
    /// <paramref name="blockNumber"/>. Such nodes are skipped by persistence, so without this
    /// signal a re-created node could be mistaken for a superseded one.
    /// </summary>
    void OnNodeRecommitted(Hash256? address, in TreePath path, Hash256 keccak, ulong blockNumber);

    /// <summary>
    /// Barrier invoked after a persistence pass has written all commit sets up to
    /// <paramref name="lastPersistedBlockNumber"/> to node storage.
    /// </summary>
    void OnSnapshotPersisted(ulong lastPersistedBlockNumber);
}
