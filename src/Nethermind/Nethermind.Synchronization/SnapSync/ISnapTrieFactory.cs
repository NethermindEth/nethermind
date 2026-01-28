// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.SnapSync;

public interface ISnapTrieFactory
{
    ISnapStateTree CreateStateTree();
    ISnapStorageTree CreateStorageTree(in ValueHash256 accountPath);

    /// <summary>
    /// Resolve node data and return the storage root hash.
    /// Used by RefreshAccounts to decode state trie node data from peers.
    /// </summary>
    /// <param name="nodeData">Raw node data from peer response</param>
    /// <returns>Storage root hash if successful, null otherwise</returns>
    Hash256? ResolveStorageRoot(byte[] nodeData);
}
