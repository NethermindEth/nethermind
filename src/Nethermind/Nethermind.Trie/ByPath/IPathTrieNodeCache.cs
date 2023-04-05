// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;
public interface IPathTrieNodeCache
{
    void AddNode(long blockNumber, TrieNode trieNode);
    TrieNode? GetNode(Keccak rootHash, byte[] path);
    TrieNode? GetNode(byte[] path, Keccak keccak);
    void SetRootHashForBlock(long blockNumber, Keccak? rootHash);
    void PersistUntilBlock(long blockNumber);
    void Prune();
    int Count { get; }
    int MaxNumberOfBlocks { get; }
}
