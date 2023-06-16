// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public interface IPatriciaTree
{
    ITrieStore TrieStore { get; }
    Keccak RootHash { get; set; }
    TrieNode? RootRef { get; set; }
    void Commit(long blockNumber, bool skipRoot = false, WriteFlags writeFlags = WriteFlags.None);
    void UpdateRootHash();
}
