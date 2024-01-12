// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie.ByPath;

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
        /// <returns></returns>
        TrieNode FindCachedOrUnknown(Hash256 hash);
        TrieNode FindCachedOrUnknown(Hash256 hash, Span<byte> nodePath, Span<byte> storagePrefix);
        TrieNode? FindCachedOrUnknown(Span<byte> nodePath, byte[] storagePrefix, Hash256? rootHash);

        /// <summary>
        /// Loads RLP of the node.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        byte[]? LoadRlp(Hash256 hash, ReadFlags flags = ReadFlags.None);
        /// <summary>
        /// Loads RLP of the node.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        byte[]? TryLoadRlp(Hash256 hash, ReadFlags flags = ReadFlags.None);
        byte[]? LoadRlp(Span<byte> nodePath, Hash256 rootHash = null);
        byte[]? TryLoadRlp(Span<byte> path, IKeyValueStore? keyValueStore);

        TrieNodeResolverCapability Capability { get; }

        bool IsPersisted(Hash256 hash, byte[] nodePathNibbles);
    }

    public enum TrieNodeResolverCapability
    {
        Hash,
        Path
    }
}
