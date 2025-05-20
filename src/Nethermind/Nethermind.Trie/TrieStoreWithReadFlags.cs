// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class TrieStoreWithReadFlags(IScopedTrieStore implementation, ReadFlags flags)
    : TrieNodeResolverWithReadFlags(implementation, flags), IScopedTrieStore
{
    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        implementation.BeginCommit(root, writeFlags);

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        implementation.IsPersisted(in path, in keccak);
}
