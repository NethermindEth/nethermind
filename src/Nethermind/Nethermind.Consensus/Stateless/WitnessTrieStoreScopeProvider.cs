// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

/// <summary>A <see cref="TrieStoreScopeProvider"/> over a witness that rejects a trie whose paths do not end at the
/// length of its hashed keys.</summary>
/// <remarks>
/// Mirrors <c>_validate_secured_trie_paths</c> of the zkEVM spec's <c>incremental_mpt.py</c>, which checks every witness
/// node reachable from a trie's root once it decodes that trie: the state trie for every block, and a storage trie once
/// its old storage is read, or written without being cleared first. Here these are the scope opening and the first load
/// of a storage tree's root, which a cleared storage tree never loads: the tree records its root as pending, and the
/// trie's node storage reports every storage node it loads. Lookups follow only the keys a block touches, so
/// the nodes off their paths are checked by walking the witness instead, which throws a <see cref="TrieException"/> on a
/// failing node. A child the witness lacks is left unchecked, as the spec leaves it hashed.
/// </remarks>
[SkipLocalsInit]
internal sealed class WitnessTrieStoreScopeProvider : TrieStoreScopeProvider
{
    private const int HashedKeyNibbles = 2 * Hash256.Size;
    private const int BranchChildren = 16;

    private readonly HashKeyedNodeStorage _witnessNodes;
    // Storage roots whose trees exist; pending until loaded, then walked once.
    private readonly OptimizedHashSet<ValueHash256> _storageRoots = [];
    private readonly OptimizedHashSet<ValueHash256> _pendingStorageRoots = [];

    // The held children of the branch being walked at each depth: every level of the walk is one nibble shallower.
    private readonly byte[]?[] _heldNodes = new byte[]?[(HashedKeyNibbles + 1) * BranchChildren];

    // Whether a node passes depends on it and its depth alone, so once the walk has read twice the witness' bytes, which
    // takes nodes reached along several paths, each pair is walked once. Otherwise a witness could reference one node from
    // every child of a branch, and the walk would grow exponentially with the depth. Bytes rather than nodes, as a node's
    // inlined children are walked with it. Twice, as the tries of a block can share a few nodes.
    private long _bytesBeforeMemo;
    private OptimizedHashSet<WalkedNode>? _walked;

    public WitnessTrieStoreScopeProvider(Witness witness, ILogManager logManager)
        : this(witness, ObserveStorageNodes(witness.CreateNodeStorage()), logManager) { }

    private WitnessTrieStoreScopeProvider(Witness witness, INodeStorage nodeStorage, ILogManager logManager)
        // Must not share nodes between lookups: the guest's TrieNode.Unseal mutates written nodes in place.
        : base(new RawTrieStore(nodeStorage), witness.CreateCodeDb(), UnavailableStateHeaderProvider.Instance, logManager)
    {
        switch (nodeStorage)
        {
            case HashKeyedNodeStorage witnessNodes:
                witnessNodes.StorageNodeLoading = OnStorageNodeLoading;
                _witnessNodes = witnessNodes;
                break;
            default:
                ((StorageNodeObservingNodeStorage)nodeStorage).StorageNodeLoading = OnStorageNodeLoading;
                _witnessNodes = new HashKeyedNodeStorage(witness.State.AsSpan());
                break;
        }

        foreach (byte[] node in witness.State.AsSpan()) _bytesBeforeMemo += 2 * node.Length;
    }

    /// <summary>Gets the trie's node storage, reporting each storage node it loads.</summary>
    /// <remarks>The guest's is a <see cref="HashKeyedNodeStorage"/>, which reports them itself. The host's is not one: the
    /// host commits storage tries in parallel.</remarks>
    private static INodeStorage ObserveStorageNodes(INodeStorage nodeStorage) =>
        nodeStorage is HashKeyedNodeStorage ? nodeStorage : new StorageNodeObservingNodeStorage(nodeStorage);

    protected override bool CanBeginScope(BlockHeader? baseBlock)
    {
        if (!base.CanBeginScope(baseBlock)) return false;

        Hash256 stateRoot = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;
        if (stateRoot != Keccak.EmptyTreeHash) ValidatePaths(stateRoot.Bytes);
        return true;
    }

    protected override StorageTree CreateStorageTree(Address address, Hash256 storageRoot)
    {
        if (storageRoot != Keccak.EmptyTreeHash && _storageRoots.Add(storageRoot.ValueHash256)) _pendingStorageRoots.Add(storageRoot.ValueHash256);
        return base.CreateStorageTree(address, storageRoot);
    }

    private void OnStorageNodeLoading(ValueHash256 keccak)
    {
        if (_pendingStorageRoots.Count != 0 && _pendingStorageRoots.Remove(keccak)) ValidatePaths(keccak.Bytes);
    }

    private void ValidatePaths(ReadOnlySpan<byte> root)
    {
        byte[]? rlp = _witnessNodes.Find(root);
        if (rlp is not null) ValidateHashedNode(root, rlp, HashedKeyNibbles);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateHashedNode(ReadOnlySpan<byte> keccak, byte[] rlp, int remainingNibbles)
    {
        if ((_bytesBeforeMemo -= rlp.Length) < 0 && !(_walked ??= []).Add(new WalkedNode(new ValueHash256(keccak), remainingNibbles))) return;

        ValidateNode(rlp, remainingNibbles);
    }

    private void ValidateNode(ReadOnlySpan<byte> rlp, int remainingNibbles)
    {
        Span<int> held = stackalloc int[BranchChildren];
        Span<byte[]?> heldNodes = _heldNodes.AsSpan(remainingNibbles * BranchChildren, BranchChildren);
        int heldCount = _witnessNodes.FindHeldBranchChildren(rlp, held, heldNodes);
        if (heldCount < 0)
        {
            ValidateOtherNode(rlp, remainingNibbles);
            return;
        }

        if (remainingNibbles == 0) ThrowPathExceedsKey();

        for (int i = 0; i < heldCount; i++)
        {
            ValidateHashedNode(rlp.Slice(held[i], Hash256.Size), heldNodes[i]!, remainingNibbles - 1);
        }
    }

    /// <summary>Validates a node other than a branch of hashed or empty children: a leaf, an extension, the empty node
    /// or a branch that inlines a child or holds a value.</summary>
    /// <remarks>Kept out of <see cref="ValidateNode"/>, whose every call would otherwise save the registers this
    /// decoding takes.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ValidateOtherNode(ReadOnlySpan<byte> rlp, int remainingNibbles)
    {
        RlpReader reader = new(rlp);
        // Not a list: the empty node.
        if (!reader.IsSequenceNext()) return;

        int end = reader.ReadSequenceLength() + reader.Position;
        int firstItem = reader.Position;
        reader.SkipItems(2);
        if (reader.Position != end)
        {
            if (remainingNibbles == 0) ThrowPathExceedsKey();

            reader.Position = firstItem;
            for (int i = 0; i < BranchChildren; i++)
            {
                ValidateChild(ref reader, remainingNibbles - 1);
            }

            return;
        }

        reader.Position = firstItem;
        ReadOnlySpan<byte> path = reader.DecodeByteArraySpan();
        // Hex-prefix encoding: the first nibble flags a leaf (0x2) and an odd nibble count (0x1), whose first nibble
        // then shares the byte.
        int nibbles = 2 * path.Length - ((path[0] & 0x10) != 0 ? 1 : 2);
        if ((path[0] & 0x20) != 0)
        {
            if (nibbles != remainingNibbles) ThrowPathExceedsKey();
        }
        else
        {
            // The spec rejects an empty extension on decode; it would not advance the walk either.
            if (nibbles == 0 || nibbles >= remainingNibbles) ThrowPathExceedsKey();

            ValidateChild(ref reader, remainingNibbles - nibbles);
        }
    }

    private void ValidateChild(ref RlpReader reader, int remainingNibbles)
    {
        if (reader.IsSequenceNext())
        {
            ReadOnlySpan<byte> inlined = reader.PeekNextItem();
            reader.SkipItem();
            _bytesBeforeMemo -= inlined.Length;
            ValidateNode(inlined, remainingNibbles);
            return;
        }

        ReadOnlySpan<byte> reference = reader.DecodeByteArraySpan();
        if (reference.Length != Hash256.Size) return;

        byte[]? rlp = _witnessNodes.Find(reference);
        if (rlp is not null) ValidateHashedNode(reference, rlp, remainingNibbles);
    }

    [DoesNotReturn]
    private static void ThrowPathExceedsKey() =>
        throw new TrieException("A witness trie path does not end at the length of its hashed keys");

    private readonly record struct WalkedNode(ValueHash256 Hash, int RemainingNibbles);

    /// <summary>The host's node storage, reporting each storage node it loads as <see cref="HashKeyedNodeStorage"/>
    /// does.</summary>
    /// <remarks>Storage tries commit in parallel, so the reports take turns.</remarks>
    private sealed class StorageNodeObservingNodeStorage(INodeStorage inner) : INodeStorage
    {
        private readonly Lock _walk = new();

        public Action<ValueHash256>? StorageNodeLoading { get; set; }

        public INodeStorage.KeyScheme Scheme
        {
            get => inner.Scheme;
            set => inner.Scheme = value;
        }

        public bool RequirePath => inner.RequirePath;

        public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
        {
            if (address is not null && StorageNodeLoading is { } storageNodeLoading)
            {
                lock (_walk) storageNodeLoading(keccak);
            }

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
