// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;
public interface IPathTrieNodeCache
{
    void AddNode(long blockNumber, TrieNode trieNode);
    TrieNode? GetNodeFromRoot(Keccak? rootHash, Span<byte> path);
    TrieNode? GetNode(Span<byte> path, Keccak keccak);
    void SetRootHashForBlock(long blockNumber, Keccak? rootHash);
    void PersistUntilBlock(long blockNumber, IBatch? batch = null);
    void Clear();
    int Count { get; }
    void AddRemovedPrefix(long blockNumber, ReadOnlySpan<byte> keyPrefix);
    bool IsPathCached(ReadOnlySpan<byte> path);
    /// <summary>
    /// For testing
    /// </summary>
    int PrefixLength { get; set; }
}
