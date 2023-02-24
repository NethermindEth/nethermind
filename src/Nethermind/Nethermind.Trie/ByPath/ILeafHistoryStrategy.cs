// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;
public interface ILeafHistoryStrategy
{
    void Init(ITrieStore trieStore);
    void AddLeafNode(long blockNumber, TrieNode trieNode);
    byte[]? GetLeafNode(Keccak rootHash, byte[] path);
    void SetRootHashForBlock(long blockNumber, Keccak? rootHash);
}
