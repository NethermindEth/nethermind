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
        TrieNode FindCachedOrUnknown(Keccak hash);
        TrieNode FindCachedOrUnknown(Keccak hash, Span<byte> nodePath, Span<byte> storagePrefix);
        TrieNode? FindCachedOrUnknown(Span<byte> nodePath, Span<byte> storagePrefix, Keccak rootHash);

        /// <summary>
        /// Loads RLP of the node.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        byte[]? LoadRlp(Keccak hash, ReadFlags flags = ReadFlags.None);
        byte[]? LoadRlp(Span<byte> nodePath, Keccak rootHash = null);
        byte[]? TryLoadRlp(Span<byte> path, IKeyValueStore? keyValueStore);

        TrieNodeResolverCapability Capability { get; }

        bool ExistsInDB(Keccak hash, byte[] nodePathNibbles);
    }

    public enum TrieNodeResolverCapability
    {
        Hash,
        Path
    }

    public static class TrieNodeResolverCapabilityExtension
    {
        public static ITrieStore CreateTrieStore(this TrieNodeResolverCapability capability, IKeyValueStoreWithBatching? keyValueStore, ILogManager? logManager)
        {
            return capability switch
            {
                TrieNodeResolverCapability.Hash => new TrieStore(keyValueStore, logManager),
                TrieNodeResolverCapability.Path => new TrieStoreByPath(keyValueStore, 0, logManager),
                _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
            };
        }

        public static ITrieStore CreateTrieStore(
            this TrieNodeResolverCapability capability,
            IKeyValueStoreWithBatching? keyValueStore,
            IPruningStrategy? pruningStrategy,
            IPersistenceStrategy? persistenceStrategy,
            ILogManager? logManager,
            ByPathStateDbPrunner dbPrunner)
        {
            return capability switch
            {
                TrieNodeResolverCapability.Hash => new TrieStore(keyValueStore, pruningStrategy, persistenceStrategy, logManager),
                TrieNodeResolverCapability.Path => new TrieStoreByPath(keyValueStore, pruningStrategy, persistenceStrategy, logManager, 0, dbPrunner),
                _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
            };
        }
    }
}
