// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// so witness-bucket reads compare the keccak word-wise. After the first write, reads first probe the
/// seeded write dictionary, whose null tombstones must override the original witness.
/// <para>
/// The masked leading bytes are unseeded, so an offline grind can crowd the same bucket across payloads.
/// Buckets larger than eight entries use the seeded overflow dictionary instead of scanning; a crowded
/// witness therefore loses the bucket optimization but cannot make the array scan unbounded. Duplicate
/// witness entries consume separate slots, and overflow entries remain retained in both representations.
/// </para>
/// <para>
/// Only the zkEVM guest uses this (see <c>WitnessNodeStorage.zkevm.cs</c>). It is not thread-safe, and
/// the host commits storage tries in parallel — <c>PersistentStorageProvider.UpdateRootHashesMultiThread</c>
/// — so the host keeps the <c>MemDb</c> form, whose dictionary is concurrent.
/// </para>
/// </remarks>
internal sealed class HashKeyedNodeStorage : INodeStorage, INodeStorage.IWriteBatch
{
    private static readonly NodeKey EmptyRootKey = new(Keccak.EmptyTreeHash.ValueHash256);

    private readonly Dictionary<NodeKey, byte[]?> _nodes = [];
    private readonly Dictionary<NodeKey, byte[]> _overflow = [];
    private readonly NodeKey[] _keys;
    private readonly byte[][] _values;
    // Per bucket, one past its newest entry's index: 0 when empty, Overflowed once it outgrew MaxBucketLength.
    private readonly int[] _heads;
    // Per entry, one past the index of the next older entry in its bucket: 0 at the end of the chain.
    private readonly int[] _next;
    private readonly int _bucketMask;
    private const int MaxBucketLength = 8;
    private const int Overflowed = -1;

    /// <param name="state">The witness' state nodes, each keyed by the keccak of its own bytes.</param>
    public HashKeyedNodeStorage(ReadOnlySpan<byte[]> state)
    {
        int count = state.Length + 1;
        int bucketCount = (int)BitOperations.RoundUpToPowerOf2((uint)count);
        // Locals rather than the fields, which the loop would reload after every keccak call.
        int bucketMask = _bucketMask = bucketCount - 1;
        int[] heads = _heads = new int[bucketCount];
        int[] next = _next = new int[count];
        NodeKey[] keys = _keys = new NodeKey[count];
        byte[][] values = _values = new byte[count][];
        state.CopyTo(values);
        values[state.Length] = [128];
        keys[state.Length] = EmptyRootKey;
        int[] lengths = new int[bucketCount];
        for (int i = 0; i < count; i++)
        {
            // Hashed straight into the key array: a returned hash would be copied twice on the way there.
            if (i != state.Length) KeccakHash.ComputeHashBytesToSpan(state[i], MemoryMarshal.AsBytes(keys.AsSpan(i, 1)));
            ref readonly NodeKey key = ref keys[i];
            int bucket = key.Bucket(bucketMask);
            int head = heads[bucket];
            if (head != Overflowed && ++lengths[bucket] <= MaxBucketLength)
            {
                next[i] = head;
                heads[bucket] = i + 1;
                continue;
            }

            _overflow[key] = values[i];
            if (head == Overflowed) continue;
            for (int entry = head; entry != 0; entry = next[entry - 1])
                _overflow[keys[entry - 1]] = values[entry - 1];
            heads[bucket] = Overflowed;
        }
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
        => Find(new NodeKey(keccak));

    private byte[]? Find(NodeKey key)
    {
        if (_nodes.Count != 0 && _nodes.TryGetValue(key, out byte[]? value)) return value;
        int entry = _heads[key.Bucket(_bucketMask)];
        if (entry == Overflowed) return _overflow.GetValueOrDefault(key);
        for (; entry != 0; entry = _next[entry - 1])
            if (_keys[entry - 1].Equals(key)) return _values[entry - 1];
        return null;
    }

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
            _nodes[key] = null;
        }
        else
        {
            _nodes[key] = data.ToArray();
        }
    }

    // The empty root is seeded, so it needs no special case here.
    public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 keccak)
        => Find(new NodeKey(keccak)) is not null;

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
