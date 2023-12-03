// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Full traditional trie store.
    /// </summary>
    public interface ITrieStore : IDisposable
    {
        void CommitNode(long blockNumber, Hash256? address, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None);

        void FinishBlockCommit(TrieType trieType, long blockNumber, Hash256? address, TrieNode? root, WriteFlags writeFlags = WriteFlags.None);

        bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak);

        IReadOnlyTrieStore AsReadOnly(INodeStorage? keyValueStore);

        event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        // Used for serving via hash
        IReadOnlyKeyValueStore TrieNodeRlpStore { get; }

        // Used by healing
        void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, byte[] rlp);

        IScopedTrieStore GetTrieStore(Hash256? address);

        TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash);
        byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
        INodeStorage.KeyScheme Scheme { get; }
    }
}
