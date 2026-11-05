// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

internal static partial class WitnessNodeStorage
{
    /// <summary>Builds a hash-keyed node storage holding the witness' state nodes.</summary>
    /// <param name="state">The witness' state nodes, each keyed by the keccak of its own bytes.</param>
    /// <remarks>Checks each node's own path, and each node it serves against its depth (see <see cref="SecuredTriePath"/>).</remarks>
    public static INodeStorage Create(IOwnedReadOnlyList<byte[]> state) => new DepthCheckingNodeStorage(CreateWitnessNodes(state));

    /// <summary>Builds a trie store over <see cref="Create"/>'s node storage that also checks each inlined node a trie
    /// operation visits against its depth, and reports each storage node it loads to <paramref name="paths"/>.</summary>
    public static ITrieStore CreateTrieStore(IOwnedReadOnlyList<byte[]> state, out WitnessTriePaths paths)
    {
        INodeStorage nodes = CreateWitnessNodes(state);
        paths = new WitnessTriePaths(nodes, state.AsSpan());
        return new DepthCheckingTrieStore(new DepthCheckingNodeStorage(nodes) { StorageNodeLoading = paths });
    }

    /// <summary>Builds the node storage holding the witness' state nodes, each checked against its own path only.</summary>
    private static INodeStorage CreateWitnessNodes(IOwnedReadOnlyList<byte[]> state)
    {
        IKeyValueStore db = MemDb.WithCapacity(state.Count);
        foreach (byte[] stateElement in state)
        {
            SecuredTriePath.ValidateWitnessNode(stateElement);
            ReadOnlySpan<byte> hash = ValueKeccak.Compute(stateElement).Bytes;
            db.Set(hash, stateElement);
        }

        return new NodeStorage(db, INodeStorage.KeyScheme.Hash);
    }

    /// <remarks>Re-implements <see cref="ITrieStore.GetTrieStore"/>, which <see cref="RawTrieStore"/> does not let a
    /// subclass override.</remarks>
    private sealed class DepthCheckingTrieStore(INodeStorage nodeStorage) : RawTrieStore(nodeStorage), ITrieStore
    {
        private readonly INodeStorage _nodeStorage = nodeStorage;

        IScopedTrieStore ITrieStore.GetTrieStore(Hash256? address) => new DepthCheckingScopedTrieStore(_nodeStorage, address);
    }

    /// <summary>Checks each node a trie walk resolves from its parent's RLP, which <see cref="DepthCheckingNodeStorage"/>
    /// never serves, against its depth.</summary>
    private sealed class DepthCheckingScopedTrieStore(INodeStorage nodeStorage, Hash256? address)
        : RawScopedTrieStore(nodeStorage, address), IEmbeddedNodeObserver
    {
        public void OnEmbeddedNodeResolving(in TreePath path, ReadOnlySpan<byte> rlp) => SecuredTriePath.ValidateAtDepth(rlp, path.Length);
    }

    /// <summary>Checks each node it serves against the depth of the path it is read at, and reports each storage node to
    /// <see cref="StorageNodeLoading"/> ahead of loading it.</summary>
    /// <remarks>The host's trie walks track the full path of the node they load, so its length is the node's depth. A node
    /// inlined in its parent is not loaded here; <see cref="DepthCheckingScopedTrieStore"/> checks it.</remarks>
    private sealed class DepthCheckingNodeStorage(INodeStorage inner) : INodeStorage
    {
        public INodeStorage.KeyScheme Scheme
        {
            get => inner.Scheme;
            set => inner.Scheme = value;
        }

        public bool RequirePath => inner.RequirePath;

        public WitnessTriePaths? StorageNodeLoading { get; init; }

        public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
        {
            if (address is not null) StorageNodeLoading?.OnStorageNodeLoading(keccak);

            byte[]? node = inner.Get(address, path, keccak, readFlags);
            if (node is not null) SecuredTriePath.ValidateAtDepth(node, path.Length);
            return node;
        }

        public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None) =>
            inner.Set(address, path, hash, data, writeFlags);

        public INodeStorage.IWriteBatch StartWriteBatch() => inner.StartWriteBatch();

        public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash) => inner.KeyExists(address, path, hash);

        public void Flush(bool onlyWal) => inner.Flush(onlyWal);

        public void Compact() => inner.Compact();
    }
}
