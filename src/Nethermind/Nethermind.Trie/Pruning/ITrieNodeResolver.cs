// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ITrieNodeResolver
    {
        /// <summary>
        /// Returns a cached and resolved <see cref="TrieNode"/> or a <see cref="TrieNode"/> with Unknown type
        /// but the hash set. The latter case allows to resolve the node later. Resolving the node means loading
        /// its RLP data from the state database.
        /// </summary>
        /// <param name="hash">Keccak hash of the RLP of the node.</param>
        /// <param name="hint">The additional context that can be used for searching the node.</param>
        /// <returns></returns>
        TrieNode FindCachedOrUnknown(Keccak hash, SearchHint hint);

        /// <summary>
        /// Loads RLP of the node.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        byte[]? LoadRlp(Keccak hash);
    }

    /// <summary>
    /// Provides a hint for the purpose of <see cref="ITrieNodeResolver.FindCachedOrUnknown"/> getting found that
    /// can be memoized further as a hint for caching the node or not.
    /// </summary>
    public enum SearchHint : byte
    {
        /// <summary>
        /// No meaningful hint can be provided about the node.
        /// </summary>
        None = 0,

        /// <summary>
        /// The node is a child node of another node.
        /// </summary>
        StorageChildNode = 1,

        /// <summary>
        /// The node is of <see cref="TrieType.Storage"/> trie.
        /// </summary>
        StorageRoot = 2,

        /// <summary>
        /// The node is a root of <see cref="TrieType.State"/> trie.
        /// </summary>
        StateRoot = 3,
    }
}
