// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public interface IPatriciaTree
{
    Keccak RootHash { get; set; }
    TrieNode? RootRef { get; set; }
    void UpdateRootHash();
    void Commit(long blockNumber, bool skipRoot = false);

    ITrieStore TrieStore { get; }
    void Accept(ITreeVisitor visitor, Keccak rootHash, VisitingOptions? visitingOptions = null);
}
