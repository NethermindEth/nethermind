// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class TrieStoreWithReadFlags(IScopedTrieStore implementation, ReadFlags flags)
    : TrieNodeResolverWithReadFlags(implementation, flags), IScopedTrieStore
{
    public ICommitter BeginCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        implementation.BeginCommit(trieType, blockNumber, root, writeFlags);

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        implementation.IsPersisted(in path, in keccak);

    public void Set(in TreePath path, in ValueHash256 keccak, byte[] rlp) =>
        implementation.Set(in path, in keccak, rlp);
}
