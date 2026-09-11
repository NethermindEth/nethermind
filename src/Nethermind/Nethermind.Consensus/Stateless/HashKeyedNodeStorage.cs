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
/// <remarks>Reads are direct hash probes. The synchronized mode is used by the host witness verifier
/// because storage tries can be committed concurrently.</remarks>
internal sealed class HashKeyedNodeStorage : INodeStorage, INodeStorage.IWriteBatch
{
    private static readonly NodeKey EmptyRootKey = new(Keccak.EmptyTreeHash.ValueHash256);

    private readonly Dictionary<NodeKey, byte[]> _nodes;
    private readonly object? _syncRoot;

    /// <param name="state">The witness' state nodes, each keyed by the keccak of its own bytes.</param>
    public HashKeyedNodeStorage(ReadOnlySpan<byte[]> state, bool threadSafe = false)
    {
        Dictionary<NodeKey, byte[]> nodes = new(state.Length + 1);

        foreach (byte[] stateElement in state)
        {
            nodes[new NodeKey(ValueKeccak.Compute(stateElement))] = stateElement;
        }

        // Some of the code does not save the empty tree at all, so the empty root has to resolve
        // whether the witness carries it or not. Seeding it here keeps the empty-root special case
        // out of every read.
        nodes[EmptyRootKey] = [128];

        _nodes = nodes;
        _syncRoot = threadSafe ? new object() : null;
    }

    public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        if (_syncRoot is null) return TryGet(keccak);
        lock (_syncRoot) return TryGet(keccak);
    }

    public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None)
    {
        if (_syncRoot is not null)
        {
            lock (_syncRoot) SetCore(keccak, data);
            return;
        }

        SetCore(keccak, data);
    }

    private void SetCore(in ValueHash256 keccak, ReadOnlySpan<byte> data)
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
    {
        if (_syncRoot is null) return _nodes.ContainsKey(new NodeKey(keccak));
        lock (_syncRoot) return _nodes.ContainsKey(new NodeKey(keccak));
    }

    private byte[]? TryGet(in ValueHash256 keccak) => _nodes.TryGetValue(new NodeKey(keccak), out byte[]? node) ? node : null;

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
    /// the guest's target.
    /// <para>
    /// The hash code goes through the run-seeded mixer rather than the keccak's own leading bytes: those
    /// are uniformly distributed, which answers accidental collisions but not a chosen witness. Unseeded,
    /// one offline grind yields node hashes sharing a bucket for every block and payload, and nothing
    /// here rejects unreachable witness nodes. See <see cref="SpanExtensions.SeedHashes"/>.
    /// </para>
    /// <para>
    /// Unlike the seed's other consumers, this one is not a fixed point: the seed commits to
    /// <c>NewPayloadRequest</c> alone, and <c>StatelessInput.Witness</c> is a sibling field outside that
    /// commitment, so node bytes can be ground against a seed that is already known. Seeding makes the
    /// grind per-payload and quadratic in the node count rather than one-time and universal; closing it
    /// properly means bounding the witness or rejecting unreachable nodes. The mixer's several multiplies
    /// per probe, against the single load the leading bytes cost, are the price of that.
    /// </para>
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

        public override int GetHashCode() =>
            (int)SpanExtensions.FastHash64For32Bytes(ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in _hash)));
    }
}
