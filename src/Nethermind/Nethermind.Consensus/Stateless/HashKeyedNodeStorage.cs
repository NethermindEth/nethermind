// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

/// <summary>A node storage that maps a witness node's keccak straight to its bytes.</summary>
/// <remarks>
/// Witness entries are placed into buckets using their leading hash bytes and compared by their complete
/// 256-bit key. Buckets with more than eight entries use a seeded overflow dictionary, so a maliciously
/// crowded witness cannot make every read scan an unbounded array. Writes are kept in a separate dictionary;
/// null entries are tombstones that override an immutable witness entry.
/// <para>
/// Duplicate witness entries consume separate bucket slots, while the overflow dictionary keeps one value per
/// key. The empty-tree RLP is seeded explicitly because witnesses may omit it.
/// </para>
/// <para>
/// The zkEVM guest uses the unsynchronized form. The host witness verifier opts into synchronization because
/// storage tries can be committed concurrently.
/// </para>
/// </remarks>
internal sealed class HashKeyedNodeStorage : INodeStorage, INodeStorage.IWriteBatch
{
    private static readonly NodeKey EmptyRootKey = new(Keccak.EmptyTreeHash.ValueHash256);

    private readonly Dictionary<NodeKey, byte[]?> _nodes = [];
    private readonly Dictionary<NodeKey, byte[]> _overflow = [];
    private readonly NodeKey[] _keys;
    private readonly byte[][] _values;
    private readonly int[] _starts;
    private readonly int _bucketMask;
    private const int MaxBucketLength = 8;
    private readonly object? _syncRoot;

    /// <param name="state">The witness' state nodes, each keyed by the keccak of its own bytes.</param>
    public HashKeyedNodeStorage(ReadOnlySpan<byte[]> state, bool threadSafe = false)
    {
        int count = state.Length + 1;
        int bucketCount = (int)BitOperations.RoundUpToPowerOf2((uint)count);
        _bucketMask = bucketCount - 1;
        _starts = new int[bucketCount + 1];
        NodeKey[] unsorted = new NodeKey[count];
        for (int i = 0; i < count; i++)
        {
            NodeKey key = i == state.Length ? EmptyRootKey : new NodeKey(ValueKeccak.Compute(state[i]));
            unsorted[i] = key;
            _starts[key.Bucket(_bucketMask) + 1]++;
        }
        for (int i = 1; i < _starts.Length; i++) _starts[i] += _starts[i - 1];
        int[] cursors = (int[])_starts.Clone();
        _keys = new NodeKey[count];
        _values = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            NodeKey key = unsorted[i];
            byte[] value = i == state.Length ? [128] : state[i];
            int bucket = key.Bucket(_bucketMask);
            int slot = cursors[bucket]++;
            _keys[slot] = key;
            _values[slot] = value;
            if (_starts[bucket + 1] - _starts[bucket] > MaxBucketLength) _overflow[key] = value;
        }
        _syncRoot = threadSafe ? new object() : null;
    }

    public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        if (_syncRoot is null) return Find(new NodeKey(keccak));
        lock (_syncRoot) return Find(new NodeKey(keccak));
    }

    private byte[]? Find(NodeKey key)
    {
        if (_nodes.Count != 0 && _nodes.TryGetValue(key, out byte[]? value)) return value;
        int bucket = key.Bucket(_bucketMask);
        int start = _starts[bucket];
        int end = _starts[bucket + 1];
        if (end - start > MaxBucketLength) return _overflow.GetValueOrDefault(key);
        for (int i = end - 1; i >= start; i--)
            if (_keys[i].Equals(key)) return _values[i];
        return null;
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
            _nodes[key] = null;
        }
        else
        {
            _nodes[key] = data.ToArray();
        }
    }

    // The empty root is seeded, so it needs no special case here.
    public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 keccak)
    {
        NodeKey key = new(keccak);
        if (_syncRoot is null) return Exists(key);
        lock (_syncRoot) return Exists(key);
    }

    private bool Exists(NodeKey key) => _nodes.TryGetValue(key, out byte[]? value) ? value is not null : Find(key) is not null;

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
    /// The overflow and write dictionaries use the hash code below; ordinary witness buckets compare
    /// full keys directly, with their scan length bounded by <see cref="MaxBucketLength"/>.
    /// </para>
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

        internal int Bucket(int mask) => (int)Unsafe.ReadUnaligned<uint>(ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in _hash))) & mask;

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
