// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
/// Only the zkEVM guest's tries use this (see <c>WitnessNodeStorage.zkevm.cs</c>). It is not thread-safe, and
/// the host commits storage tries in parallel — <c>PersistentStorageProvider.UpdateRootHashesMultiThread</c>
/// — so the host keeps the <c>MemDb</c> form, whose dictionary is concurrent, and reads one of these only for
/// <see cref="WitnessTrieStoreScopeProvider"/>'s walk of the witness.
/// </para>
/// </remarks>
internal sealed class HashKeyedNodeStorage : INodeStorage, INodeStorage.IWriteBatch
{
    private static readonly NodeKey EmptyRootKey = new(ValueKeccak.EmptyTreeHash);

    // Created by the first write, so a probe tests a reference rather than the map's count, two narrow loads in the guest.
    private OptimizedDictionary<NodeKey, byte[]?>? _nodes;
    private readonly OptimizedDictionary<NodeKey, byte[]> _overflow = [];
    private readonly NodeKey[] _keys;
    private readonly byte[][] _values;
    // The heads, links and mask are word-sized: the guest pays several times an aligned load for a narrow one.
    // Per bucket, one past its newest entry's index above LengthBits and the bucket's length below them:
    // 0 when empty, Overflowed once it outgrew MaxBucketLength.
    private readonly nint[] _heads;
    // Per entry, one past the index of the next older entry in its bucket: 0 at the end of the chain.
    private readonly nint[] _next;
    private readonly nint _bucketMask;
    private const int MaxBucketLength = 8;
    private const int Overflowed = -1;
    private const int LengthBits = 4;
    private const nint LengthMask = (1 << LengthBits) - 1;

    /// <param name="state">The witness' state nodes, each keyed by the keccak of its own bytes.</param>
    public HashKeyedNodeStorage(ReadOnlySpan<byte[]> state)
    {
        int count = state.Length + 1;
        int bucketCount = (int)BitOperations.RoundUpToPowerOf2((uint)count);
        // Locals rather than the fields, which the loop would reload after every call out of it.
        nint bucketMask = _bucketMask = bucketCount - 1;
        nint[] heads = _heads = new nint[bucketCount];
        nint[] next = _next = new nint[count];
        NodeKey[] keys = _keys = new NodeKey[count];
        byte[][] values = _values = new byte[count][];
        state.CopyTo(values);
        values[state.Length] = [128];
        keys[state.Length] = EmptyRootKey;
        // Hashed in a method of its own so the bucketing loop keeps its locals in registers.
        HashNodes(values, state.Length, keys);

        // Unchecked: the mask keeps a bucket below bucketCount and every array is count long, while the
        // bounds checks would load each array's 32-bit length, a narrow access, per node.
        Debug.Assert(heads.Length == bucketMask + 1 && next.Length == count && keys.Length == count,
            "The unchecked bucketing loop relies on these sizes");
        Debug.Assert(MaxBucketLength < LengthMask, "A full bucket's length must stay below the bits Overflowed sets");
        ref NodeKey firstKey = ref MemoryMarshal.GetArrayDataReference(keys);
        ref nint firstHead = ref MemoryMarshal.GetArrayDataReference(heads);
        ref nint firstNext = ref MemoryMarshal.GetArrayDataReference(next);
        for (nint i = 0; i < count; i++)
        {
            ref readonly NodeKey key = ref Unsafe.Add(ref firstKey, i);
            nint bucket = key.Bucket(bucketMask);
            ref nint headSlot = ref Unsafe.Add(ref firstHead, bucket);
            nint head = headSlot;
            nint length = head & LengthMask;
            // Overflowed has every length bit set, so it fails the length check too.
            if (length < MaxBucketLength)
            {
                Unsafe.Add(ref firstNext, i) = (nint)((nuint)head >> LengthBits);
                headSlot = ((i + 1) << LengthBits) + length + 1;
                continue;
            }

            _overflow[key] = values[i];
            if (head == Overflowed) continue;
            for (nint entry = (nint)((nuint)head >> LengthBits); entry != 0; entry = next[entry - 1])
                _overflow[keys[entry - 1]] = values[entry - 1];
            heads[bucket] = Overflowed;
        }
    }

    /// <summary>Keys each of the first <paramref name="count"/> of <paramref name="nodes"/> by its keccak, at the same index of <paramref name="keys"/>.</summary>
    /// <remarks>Through <see cref="KeccakHash.ComputeHash256OfWitnessNodes"/>, which lets the commit re-hash an edited
    /// branch from its first changed rate block; <see cref="Find"/> tells it which node it hands out.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HashNodes(byte[][] nodes, int count, Span<NodeKey> keys)
    {
        Debug.Assert(Unsafe.SizeOf<NodeKey>() == Unsafe.SizeOf<ValueHash256>(), "NodeKey is reinterpreted as its keccak");
        KeccakHash.ComputeHash256OfWitnessNodes(nodes, count, MemoryMarshal.Cast<NodeKey, ValueHash256>(keys));
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
    /// The keccak is viewed as a <see cref="NodeKey"/>, which wraps it alone, rather than copied into one: a copy
    /// holds its four words in callee-saved registers across the whole inlined resolve.
    /// </remarks>
    public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        // Ahead of the load, whose witness tag the trie node reads straight after it.
        if (address is not null) StorageNodeLoading?.Invoke(keccak);

        return Find(in Unsafe.As<ValueHash256, NodeKey>(ref Unsafe.AsRef(in keccak)));
    }

    /// <summary>Called with the keccak of a storage trie's node before <see cref="Get"/> loads it.</summary>
    /// <remarks>By keccak alone: the guest's trie leaves the path untracked, as nothing here reads it.</remarks>
    internal Action<ValueHash256>? StorageNodeLoading { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[]? Find(in NodeKey key)
    {
        if (_nodes is not null && _nodes.TryGetValue(key, out byte[]? value)) return value;
        // Buckets are masked into the heads and chains only link stored entries, so the arrays are read unchecked.
        nint head = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_heads), key.Bucket(_bucketMask));
        if (head == Overflowed) return _overflow.TryGetValue(key, out byte[]? overflowed) ? overflowed : null;
        for (nint entry = (nint)((nuint)head >> LengthBits); entry != 0; entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_next), entry - 1))
            if (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_keys), entry - 1).Equals(key))
            {
                KeccakHash.NoteWitnessNodeLoaded(entry);
                return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_values), entry - 1);
            }
        return null;
    }

    /// <summary>Gets the node whose keccak is <paramref name="keccak"/>, as <see cref="Get"/> does.</summary>
    /// <remarks>
    /// Until the first write this probes the keccak in place rather than copying it into a <see cref="NodeKey"/>: the
    /// keccak's leading four bytes pick its bit of <see cref="_filter"/> and its bucket, and reject most of the bucket's
    /// entries before the full comparison.
    /// <para>Unlike <see cref="Get"/>, the probe tags no load: it serves no trie node.</para>
    /// </remarks>
    internal byte[]? Find(ReadOnlySpan<byte> keccak) =>
        _nodes is null
            ? new HeldProbe(this, _filter ??= BuildFilter()).Find(ref MemoryMarshal.GetReference(keccak))
            : Find(new NodeKey(new ValueHash256(keccak)));

    /// <summary>Finds which children of a branch node this storage holds, for a branch whose sixteen children are each
    /// a keccak or empty and whose value is empty, the shape of nearly every branch.</summary>
    /// <param name="rlp">The RLP of the node.</param>
    /// <param name="held">Receives the offset in <paramref name="rlp"/> of each held child's keccak, in child order.</param>
    /// <param name="nodes">Receives each held child, at its index in <paramref name="held"/>.</param>
    /// <returns>How many offsets were written to <paramref name="held"/>, or -1 if <paramref name="rlp"/> is not a list
    /// of that shape.</returns>
    /// <remarks>Probes as <see cref="Find(ReadOnlySpan{byte})"/> does, while parsing: a branch references sixteen
    /// children, few of which a witness holds, so a bit of <see cref="_filter"/> rejects most of them first.</remarks>
    internal int FindHeldBranchChildren(ReadOnlySpan<byte> rlp, Span<int> held, Span<byte[]?> nodes)
    {
        ulong[] filter = _filter ??= BuildFilter();
        if (_nodes is null && rlp.Length == FullBranchLength && rlp[0] == 0xf9 && rlp[1] == 0x02 && rlp[2] == 0x11 && rlp[^1] == EmptyItem)
        {
            int count = FindHeldFullBranchChildren(ref MemoryMarshal.GetReference(rlp), filter, held, nodes);
            if (count >= 0) return count;
        }

        return FindHeldListChildren(rlp, filter, held, nodes);
    }

    /// <summary><see cref="FindHeldBranchChildren"/> for a node of <see cref="FullBranchLength"/> bytes, starting at
    /// <paramref name="first"/>, before the first write.</summary>
    /// <remarks>Apart from the general case, which would make every call save the registers of its own parsing.</remarks>
    private int FindHeldFullBranchChildren(ref byte first, ulong[] filter, Span<int> held, Span<byte[]?> nodes)
    {
        int candidates = FilterFullBranchChildren(ref first, filter);
        if (candidates <= 0) return candidates;

        HeldProbe probe = new(this, filter);
        int count = 0;
        do
        {
            int offset = FullBranchFirstKeccak + BitOperations.TrailingZeroCount(candidates) * KeccakItemLength;
            byte[]? node = probe.FindFiltered(ref Unsafe.Add(ref first, offset));
            if (node is not null)
            {
                held[count] = offset;
                nodes[count++] = node;
            }

            candidates &= candidates - 1;
        } while (candidates != 0);

        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int FindHeldListChildren(ReadOnlySpan<byte> rlp, ulong[] filter, Span<int> held, Span<byte[]?> nodes)
    {
        if (rlp.IsEmpty) return -1;

        HeldProbe probe = new(this, filter);
        ref byte first = ref MemoryMarshal.GetReference(rlp);
        int count = 0;
        byte[]? node;

        (int start, int length) = rlp[0] switch
        {
            < 0xc0 => (-1, 0),
            <= 0xf7 => (1, rlp[0] - 0xc0),
            0xf8 when rlp.Length > 1 => (2, rlp[1]),
            0xf9 when rlp.Length > 2 => (3, rlp[1] << 8 | rlp[2]),
            _ => (-1, 0)
        };
        if (start < 0 || start + length != rlp.Length) return -1;

        int end = rlp.Length;
        int position = start;
        for (int i = 0; i < BranchChildren; i++)
        {
            if (position >= end) return -1;

            byte prefix = Unsafe.Add(ref first, position);
            if (prefix == EmptyItem)
            {
                position++;
                continue;
            }

            if (prefix != KeccakItem || position + KeccakItemLength > end) return -1;

            if ((node = probe.Find(ref Unsafe.Add(ref first, position + 1))) is not null)
            {
                held[count] = position + 1;
                nodes[count++] = node;
            }

            position += KeccakItemLength;
        }

        // Only an empty value ends the list there.
        return position == end - 1 && Unsafe.Add(ref first, position) == EmptyItem ? count : -1;
    }

    private const int BranchChildren = 16;
    private const byte EmptyItem = 0x80;
    private const byte KeccakItem = 0x80 + Hash256.Size;
    private const int KeccakItemLength = 1 + Hash256.Size;
    // A list prefix of three bytes, sixteen keccaks and an empty value.
    private const int FullBranchLength = KeccakHash.Hash532InputLength;
    private const int FullBranchFirstKeccak = 4;
    // Enough bits that a keccak the witness lacks rarely finds its bit set.
    private const int FilterBitsPerNode = 16;

    // Of the keccaks' leading bits, one per witness node; built on first use, as only the witness walk reads it.
    private ulong[]? _filter;

    /// <summary>Gets which children of a node of <see cref="FullBranchLength"/> bytes, starting at
    /// <paramref name="first"/>, have their bit of <paramref name="filter"/> set, as a bitmask over the children.</summary>
    /// <returns>The bitmask, or -1 if an item is not a keccak: its length and list prefix alone would not tell a branch
    /// of sixteen keccaks, as other items could add up to the same length.</returns>
    /// <remarks>A pass of its own, keeping only the filter live: with the probe's arrays too, the loop spills.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FilterFullBranchChildren(ref byte first, ulong[] filter)
    {
        ref ulong words = ref MemoryMarshal.GetArrayDataReference(filter);
        nint mask = filter.Length * 64 - 1;
        ref byte keccak = ref Unsafe.Add(ref first, FullBranchFirstKeccak);
        int candidates = 0;
        // Any bit but the keccak prefix's, collected rather than branched on.
        int otherPrefixBits = 0;
        for (int child = 0; child < BranchChildren; child++)
        {
            otherPrefixBits |= Unsafe.Add(ref keccak, -1) ^ KeccakItem;
            nint bit = (nint)Unsafe.ReadUnaligned<uint>(ref keccak) & mask;
            candidates |= (int)(Unsafe.Add(ref words, bit >> 6) >> (int)(bit & 63) & 1) << child;
            keccak = ref Unsafe.Add(ref keccak, KeccakItemLength);
        }

        return otherPrefixBits == 0 ? candidates : -1;
    }

    private ulong[] BuildFilter()
    {
        int bits = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, _keys.Length * FilterBitsPerNode));
        ulong[] filter = new ulong[bits / 64];
        nint mask = bits - 1;
        foreach (NodeKey key in _keys)
        {
            nint bit = (nint)key.Leading & mask;
            filter[bit >> 6] |= 1UL << (int)(bit & 63);
        }

        return filter;
    }

    /// <summary>Finds nodes by keccak with the storage's arrays in locals, which a loop would otherwise reload for every
    /// keccak.</summary>
    private readonly ref struct HeldProbe(HashKeyedNodeStorage storage, ulong[] filter)
    {
        private readonly HashKeyedNodeStorage _storage = storage;
        private readonly bool _unwritten = storage._nodes is null;
        private readonly ref ulong _filter = ref MemoryMarshal.GetArrayDataReference(filter);
        private readonly nint _filterMask = filter.Length * 64 - 1;
        private readonly ref nint _heads = ref MemoryMarshal.GetArrayDataReference(storage._heads);
        private readonly nint _bucketMask = storage._bucketMask;
        private readonly ref nint _next = ref MemoryMarshal.GetArrayDataReference(storage._next);
        private readonly ref NodeKey _keys = ref MemoryMarshal.GetArrayDataReference(storage._keys);
        private readonly ref byte[] _values = ref MemoryMarshal.GetArrayDataReference(storage._values);

        /// <param name="keccak">The first of the keccak's 32 bytes.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte[]? Find(ref byte keccak)
        {
            // The filter has only the witness' own nodes.
            if (!_unwritten) return _storage.Find(MemoryMarshal.CreateReadOnlySpan(ref keccak, Hash256.Size));

            // Unaligned: a child reference lies at an odd offset of its node. Four bytes cover both masks, which an array
            // length bounds below 2^31.
            nint bit = (nint)Unsafe.ReadUnaligned<uint>(ref keccak) & _filterMask;
            return (Unsafe.Add(ref _filter, bit >> 6) >> (int)(bit & 63) & 1) == 0 ? null : FindFiltered(ref keccak);
        }

        /// <summary>Finds a keccak whose bit of the filter is set, before the first write.</summary>
        /// <param name="keccak">The first of the keccak's 32 bytes.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte[]? FindFiltered(ref byte keccak)
        {
            uint leading = Unsafe.ReadUnaligned<uint>(ref keccak);
            nint head = Unsafe.Add(ref _heads, (nint)leading & _bucketMask);
            if (head == Overflowed) return _storage.FindOverflowed(ref keccak);

            for (nint entry = (nint)((nuint)head >> LengthBits); entry != 0; entry = Unsafe.Add(ref _next, entry - 1))
            {
                ref readonly NodeKey key = ref Unsafe.Add(ref _keys, entry - 1);
                if (key.Leading == leading && key.Equals(MemoryMarshal.CreateReadOnlySpan(ref keccak, Hash256.Size)))
                    return Unsafe.Add(ref _values, entry - 1);
            }

            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[]? FindOverflowed(ref byte keccak) =>
        _overflow.TryGetValue(new NodeKey(new ValueHash256(MemoryMarshal.CreateReadOnlySpan(ref keccak, Hash256.Size))), out byte[]? overflowed) ? overflowed : null;

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

        _nodes ??= [];
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

        internal nint Bucket(nint mask) => (nint)Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in _hash))) & mask;

        /// <summary>The keccak's leading four bytes, little-endian.</summary>
        internal uint Leading => Unsafe.As<ValueHash256, uint>(ref Unsafe.AsRef(in _hash));

        public bool Equals(ReadOnlySpan<byte> keccak) =>
            MemoryMarshal.AsBytes(new ReadOnlySpan<ValueHash256>(in _hash)).SequenceEqual(keccak);

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
