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
    public static INodeStorage Create(IOwnedReadOnlyList<byte[]> state)
    {
        IKeyValueStore db = MemDb.WithCapacity(state.Count);
        foreach (byte[] stateElement in state)
        {
            ReadOnlySpan<byte> hash = ValueKeccak.Compute(stateElement).Bytes;
            db.Set(hash, stateElement);
        }

        return new NodeStorage(db, INodeStorage.KeyScheme.Hash);
    }

    /// <summary>Builds a trie store over <see cref="Create"/>'s node storage, which reports each storage node it loads to
    /// <paramref name="paths"/>.</summary>
    public static ITrieStore CreateTrieStore(IOwnedReadOnlyList<byte[]> state, out WitnessTriePaths paths)
    {
        INodeStorage nodes = Create(state);
        paths = new WitnessTriePaths(nodes, state.AsSpan());
        return new RawTrieStore(new StorageLoadReportingNodeStorage(nodes, paths));
    }

    /// <summary>Reports each storage node it serves to <paramref name="paths"/> ahead of loading it.</summary>
    private sealed class StorageLoadReportingNodeStorage(INodeStorage inner, WitnessTriePaths paths) : INodeStorage
    {
        public INodeStorage.KeyScheme Scheme
        {
            get => inner.Scheme;
            set => inner.Scheme = value;
        }

        public bool RequirePath => inner.RequirePath;

        public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
        {
            if (address is not null) paths.OnStorageNodeLoading(keccak);

            return inner.Get(address, path, keccak, readFlags);
        }

        public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None) =>
            inner.Set(address, path, hash, data, writeFlags);

        public INodeStorage.IWriteBatch StartWriteBatch() => inner.StartWriteBatch();

        public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash) => inner.KeyExists(address, path, hash);

        public void Flush(bool onlyWal) => inner.Flush(onlyWal);

        public void Compact() => inner.Compact();
    }
}
