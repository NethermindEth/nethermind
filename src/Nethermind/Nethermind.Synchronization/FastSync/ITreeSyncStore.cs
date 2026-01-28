// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Synchronization.FastSync;

/// <summary>
/// High-level storage interface for TreeSync that abstracts both storage operations
/// and verification operations. Allows different backends (Patricia, Flat) to provide
/// completely different implementations.
/// </summary>
public interface ITreeSyncStore
{
    /// <summary>
    /// Check if a trie node exists in storage.
    /// </summary>
    bool NodeExists(Hash256? address, in TreePath path, in ValueHash256 hash);

    /// <summary>
    /// Save a trie node to storage.
    /// </summary>
    /// <param name="address">Storage address for storage tries, null for state trie.</param>
    /// <param name="path">The path to this node in the trie.</param>
    /// <param name="hash">The hash of the node data.</param>
    /// <param name="data">The RLP-encoded node data.</param>
    void SaveNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data);

    /// <summary>
    /// Called when sync is complete and state should be finalized and flushed.
    /// </summary>
    /// <param name="pivotHeader">The block header containing the synced state root.</param>
    void FinalizeSync(BlockHeader pivotHeader);

    /// <summary>
    /// Create a verification context for checking storage roots during sync.
    /// The context is created with root node data that hasn't been persisted yet.
    /// </summary>
    ITreeSyncVerificationContext CreateVerificationContext(byte[] rootNodeData);
}

/// <summary>
/// Context for verifying storage roots during sync.
/// Allows querying accounts from in-flight (not yet persisted) trie data.
/// </summary>
public interface ITreeSyncVerificationContext
{
    /// <summary>
    /// Get an account by its address hash for verification purposes.
    /// </summary>
    Account? GetAccount(Hash256 addressHash);
}
