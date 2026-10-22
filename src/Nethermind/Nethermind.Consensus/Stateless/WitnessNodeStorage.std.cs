// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

internal static partial class WitnessNodeStorage
{
    /// <summary>Builds a hash-keyed node storage holding the witness' state nodes.</summary>
    /// <param name="state">The witness' state nodes, each keyed by the keccak of its own bytes.</param>
    /// <remarks>Checks each node's own path, and each node it serves against its depth (see <see cref="SecuredTriePath"/>).</remarks>
    public static INodeStorage Create(IOwnedReadOnlyList<byte[]> state)
    {
        IKeyValueStore db = MemDb.WithCapacity(state.Count);
        foreach (byte[] stateElement in state)
        {
            SecuredTriePath.ValidateWitnessNode(stateElement);
            ReadOnlySpan<byte> hash = ValueKeccak.Compute(stateElement).Bytes;
            db.Set(hash, stateElement);
        }

        return new DepthCheckingNodeStorage(new NodeStorage(db, INodeStorage.KeyScheme.Hash));
    }

    /// <summary>Checks each node it serves against the depth of the path it is read at.</summary>
    /// <remarks>The host's trie walks track the full path of the node they load, so its length is the node's depth. A node
    /// inlined in its parent is not loaded here and goes unchecked.</remarks>
    private sealed class DepthCheckingNodeStorage(INodeStorage inner) : INodeStorage
    {
        public INodeStorage.KeyScheme Scheme
        {
            get => inner.Scheme;
            set => inner.Scheme = value;
        }

        public bool RequirePath => inner.RequirePath;

        public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
        {
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
