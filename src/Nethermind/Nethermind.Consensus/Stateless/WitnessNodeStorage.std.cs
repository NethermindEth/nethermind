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
    /// <remarks>Checks each node it serves at the depth it is read at (see <see cref="SecuredTrieNodeValidator"/>).</remarks>
    public static INodeStorage Create(IOwnedReadOnlyList<byte[]> state) => CreateValidating(state);

    /// <summary>Builds a trie store over <see cref="Create"/>'s node storage that also checks each embedded node a trie
    /// operation reaches.</summary>
    public static ITrieStore CreateTrieStore(IOwnedReadOnlyList<byte[]> state) => new ValidatingTrieStore(CreateValidating(state));

    private static ValidatingNodeStorage CreateValidating(IOwnedReadOnlyList<byte[]> state)
    {
        IKeyValueStore db = MemDb.WithCapacity(state.Count);
        foreach (byte[] stateElement in state)
        {
            ReadOnlySpan<byte> hash = ValueKeccak.Compute(stateElement).Bytes;
            db.Set(hash, stateElement);
        }

        return new ValidatingNodeStorage(new NodeStorage(db, INodeStorage.KeyScheme.Hash));
    }

    /// <remarks>Re-implements <see cref="ITrieStore.GetTrieStore"/>, which <see cref="RawTrieStore"/> does not let a
    /// subclass override.</remarks>
    private sealed class ValidatingTrieStore(ValidatingNodeStorage nodeStorage) : RawTrieStore(nodeStorage), ITrieStore
    {
        IScopedTrieStore ITrieStore.GetTrieStore(Hash256? address) => new ValidatingScopedTrieStore(nodeStorage, address);
    }

    /// <summary>Checks each node a trie walk resolves from its parent's RLP, which <see cref="ValidatingNodeStorage"/> never serves.</summary>
    private sealed class ValidatingScopedTrieStore(ValidatingNodeStorage nodeStorage, Hash256? address)
        : RawScopedTrieStore(nodeStorage, address), IEmbeddedNodeObserver
    {
        private readonly ValidatingNodeStorage _nodeStorage = nodeStorage;
        private readonly Hash256? _address = address;

        public void OnEmbeddedNodeResolving(in TreePath path, ReadOnlySpan<byte> rlp) => _nodeStorage.Validate(_address, path, rlp, isHashed: false);
    }

    /// <summary>Checks each node it serves at the depth of the path it is read at.</summary>
    /// <remarks>The host's trie walks track the full path of the node they load, so its length is the node's depth. A node
    /// embedded in its parent is not loaded here; <see cref="ValidatingScopedTrieStore"/> checks it.</remarks>
    private sealed class ValidatingNodeStorage(INodeStorage inner) : INodeStorage
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
            if (node is not null) Validate(address, path, node, isHashed: true);
            return node;
        }

        /// <summary>Checks <paramref name="rlp"/>, read at <paramref name="path"/>, and the child of an extension.</summary>
        /// <remarks>
        /// The host's walks do not tell the storage that a node they load is an extension's child, so the child is checked to be
        /// a branch once the extension is reached instead. A hashed child missing from the witness is left to the walk that
        /// reaches it, as a collapse onto the extension does not.
        /// </remarks>
        public void Validate(Hash256? address, in TreePath path, ReadOnlySpan<byte> rlp, bool isHashed)
        {
            SecuredTrieNodeValidator.Validate(rlp, path.Length, isHashed);
            if (!SecuredTrieNodeValidator.TryGetExtensionChild(rlp, out ReadOnlySpan<byte> child, out bool isChildHashed)) return;

            ReadOnlySpan<byte> childRlp = isChildHashed ? inner.Get(address, path, new ValueHash256(child)) : child;
            if (!childRlp.IsEmpty) SecuredTrieNodeValidator.ValidateExtensionChild(childRlp);
        }

        public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None) =>
            inner.Set(address, path, hash, data, writeFlags);

        public INodeStorage.IWriteBatch StartWriteBatch() => inner.StartWriteBatch();

        public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash) => inner.KeyExists(address, path, hash);

        public void Flush(bool onlyWal) => inner.Flush(onlyWal);

        public void Compact() => inner.Compact();
    }
}
