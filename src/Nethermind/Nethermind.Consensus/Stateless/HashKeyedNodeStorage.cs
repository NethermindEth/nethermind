// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

/// <summary>A node storage that maps a witness node's keccak straight to its bytes.</summary>
/// <remarks>
/// The alternative is a <c>MemDb</c> behind <see cref="NodeStorage"/>, which keys a dictionary by
/// <c>byte[]</c>: every witness node pays a key-array allocation on load, and every read builds a
/// key span, hashes its bytes and compares them against the stored array. Here the keccak is the key,
/// so a read is one word-wise probe.
/// <para>
/// Only the zkEVM guest uses this (see <c>WitnessNodeStorage.zkevm.cs</c>). It is not thread-safe, and
/// the host commits storage tries in parallel — <c>PersistentStorageProvider.UpdateRootHashesMultiThread</c>
/// — so the host keeps the <c>MemDb</c> form, whose dictionary is concurrent.
/// </para>
/// </remarks>
internal sealed class HashKeyedNodeStorage : INodeStorage, INodeStorage.IWriteBatch
{
    private static readonly NodeKey EmptyRootKey = new(Keccak.EmptyTreeHash.ValueHash256);

    private readonly Dictionary<NodeKey, byte[]> _nodes;

    /// <param name="state">The witness' state nodes, each keyed by the keccak of its own bytes.</param>
    public HashKeyedNodeStorage(ReadOnlySpan<byte[]> state)
    {
        Dictionary<NodeKey, byte[]> nodes = new(state.Length + 1);

        foreach (byte[] stateElement in state)
        {
            nodes[new NodeKey(ValueKeccak.Compute(stateElement))] = stateElement;
        }

        // Some of the code does not save the empty tree at all, so the empty root has to resolve
        // whether the witness carries it or not. Seeding it here keeps NodeStorage's special case
        // out of every read.
        nodes[EmptyRootKey] = [128];

        _nodes = nodes;
    }

    /// <inheritdoc/>
    /// <remarks>The scheme is fixed: only <c>FullPruner</c> reassigns it, and it does not run in the guest.</remarks>
    public INodeStorage.KeyScheme Scheme
    {
        get => INodeStorage.KeyScheme.Hash;
        set => throw new NotSupportedException();
    }

    public bool RequirePath => true;

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="NodeStorage"/> falls back to a half-path key when the hash key misses. Nothing writes
    /// a half-path key under <see cref="INodeStorage.KeyScheme.Hash"/>, so that probe can only miss here.
    /// </remarks>
    public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
        => _nodes.TryGetValue(new NodeKey(keccak), out byte[]? node) ? node : null;

    /// <inheritdoc/>
    /// <remarks>
    /// Null <paramref name="data"/> evicts the node. <see cref="NodeStorage"/>'s direct <c>Set</c> keeps
    /// the hash-keyed entry and removes only the half-path one, but every stateless write arrives
    /// through <see cref="INodeStorage.IWriteBatch"/>, whose <see cref="NodeStorage"/> form removes the
    /// hash key as well.
    /// </remarks>
    public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None)
    {
        NodeKey key = new(keccak);
        if (key.Equals(EmptyRootKey))
        {
            return;
        }

        if (data.IsNull())
        {
            _nodes.Remove(key);
        }
        else
        {
            _nodes[key] = data.ToArray();
        }
    }

    // The empty root is seeded, so it needs no special case here.
    public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 keccak)
        => _nodes.ContainsKey(new NodeKey(keccak));

    public INodeStorage.IWriteBatch StartWriteBatch() => this;

    void INodeStorage.IWriteBatch.Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags)
        => Set(address, path, currentNodeKeccak, data, writeFlags);

    // The store outlives every batch taken on it.
    public void Dispose() { }

    public void Flush(bool onlyWal) { }

    public void Compact() { }

    /// <summary>A node keccak as a dictionary key.</summary>
    /// <remarks>
    /// Equality is spelled out word-wise rather than deferred to
    /// <see cref="ValueHash256.Equals(ValueHash256)"/>, which compares
    /// <see cref="System.Runtime.Intrinsics.Vector256{T}"/>s and so expands to a byte-at-a-time loop on
    /// the guest's target. The hash code is the keccak's own leading bytes: they are already uniformly
    /// distributed, so <see cref="ValueHash256.GetHashCode"/>'s re-mix of all 32 buys nothing. Reading
    /// the leading word and truncating it instead measured 0.15% worse.
    /// </remarks>
    private readonly struct NodeKey(in ValueHash256 hash) : IEquatable<NodeKey>
    {
        private readonly ValueHash256 _hash = hash;

        public bool Equals(NodeKey other)
        {
            ref byte left = ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in _hash));
            ref byte right = ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in other._hash));

            return Unsafe.ReadUnaligned<ulong>(ref left) == Unsafe.ReadUnaligned<ulong>(ref right)
                   && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref left, 8)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref right, 8))
                   && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref left, 16)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref right, 16))
                   && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref left, 24)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref right, 24));
        }

        public override bool Equals(object? obj) => obj is NodeKey other && Equals(other);

        public override int GetHashCode() => Unsafe.As<ValueHash256, int>(ref Unsafe.AsRef(in _hash));
    }
}
