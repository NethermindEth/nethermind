// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class HashKeyedNodeStorageTests
{
    // The first node is the empty tree, which the constructor seeds whether the witness carries it or not.
    private static readonly byte[][] Nodes =
    [
        [0x80],
        [0xc1, 0x80],
        [0xf8, 0x44, 0x01, 0x02, 0x03],
        new byte[64]
    ];

    private static readonly ValueHash256 UnknownHash = ValueKeccak.Compute("not in the witness");

    private static HashKeyedNodeStorage Storage(params byte[][] state) => new(state);

    private static ValueHash256 HashOf(byte[] node) => ValueKeccak.Compute(node);

    [Test]
    public void Resolves_every_witness_node_by_its_own_hash()
    {
        HashKeyedNodeStorage storage = Storage(Nodes);

        foreach (byte[] node in Nodes)
        {
            Assert.That(storage.Get(null, TreePath.Empty, HashOf(node)), Is.EqualTo(node));
            Assert.That(storage.KeyExists(null, TreePath.Empty, HashOf(node)), Is.True);
        }
    }

    [Test]
    public void Misses_an_unknown_hash()
    {
        HashKeyedNodeStorage storage = Storage(Nodes);

        Assert.That(storage.Get(null, TreePath.Empty, UnknownHash), Is.Null);
        Assert.That(storage.KeyExists(null, TreePath.Empty, UnknownHash), Is.False);
    }

    [Test]
    public void Resolves_the_empty_root_when_the_witness_omits_it()
    {
        HashKeyedNodeStorage storage = Storage();

        Assert.That(HashOf([128]), Is.EqualTo(Keccak.EmptyTreeHash.ValueHash256), "the seeded node must be the empty tree");
        Assert.That(storage.Get(null, TreePath.Empty, Keccak.EmptyTreeHash.ValueHash256), Is.EqualTo(new byte[] { 128 }));
        Assert.That(storage.KeyExists(null, TreePath.Empty, Keccak.EmptyTreeHash.ValueHash256), Is.True);
    }

    [Test]
    public void Round_trips_a_written_node([Values] bool throughBatch)
    {
        HashKeyedNodeStorage storage = Storage();
        byte[] node = [0xc2, 0x01, 0x02];
        ValueHash256 hash = HashOf(node);

        Write(storage, throughBatch, hash, node);

        Assert.That(storage.Get(null, TreePath.Empty, hash), Is.EqualTo(node));
        Assert.That(storage.KeyExists(null, TreePath.Empty, hash), Is.True);
    }

    [Test]
    public void Evicts_a_node_written_with_no_data([Values] bool throughBatch)
    {
        byte[] node = Nodes[2];
        ValueHash256 hash = HashOf(node);
        HashKeyedNodeStorage storage = Storage(node);

        Write(storage, throughBatch, hash, null);

        Assert.That(storage.Get(null, TreePath.Empty, hash), Is.Null);
        Assert.That(storage.KeyExists(null, TreePath.Empty, hash), Is.False);
    }

    [Test]
    public void Keeps_the_seeded_empty_root_whatever_is_written_to_it([Values] bool remove)
    {
        HashKeyedNodeStorage storage = Storage();

        byte[] data = remove ? null : [0xff];

        storage.Set(null, TreePath.Empty, Keccak.EmptyTreeHash.ValueHash256, data);

        Assert.That(storage.Get(null, TreePath.Empty, Keccak.EmptyTreeHash.ValueHash256), Is.EqualTo(new byte[] { 128 }));
    }

    [Test]
    public void Separates_keys_that_share_a_hash_code([Range(0, 7)] int half)
    {
        (ValueHash256 first, ValueHash256 second) = FindHashCodeCollision(half);

        HashKeyedNodeStorage storage = Storage();
        storage.Set(null, TreePath.Empty, first, [0x01]);
        storage.Set(null, TreePath.Empty, second, [0x02]);

        Assert.That(storage.Get(null, TreePath.Empty, first), Is.EqualTo(new byte[] { 0x01 }));
        Assert.That(storage.Get(null, TreePath.Empty, second), Is.EqualTo(new byte[] { 0x02 }));
    }

    [Test]
    public void Resolves_colliding_witness_buckets_before_and_after_writes([Values(2, 8, 9, 16)] int count)
    {
        int mask = (int)BitOperations.RoundUpToPowerOf2((uint)count + 1) - 1;
        int bucket = ((int)(BitConverter.ToUInt32(Keccak.EmptyTreeHash.Bytes) & (uint)mask) + 1) & mask;
        byte[][] nodes = new byte[count + 1][];
        int found = 0;
        for (int candidate = 0; found < nodes.Length; candidate++)
        {
            byte[] node = BitConverter.GetBytes(candidate);
            if ((BitConverter.ToUInt32(HashOf(node).Bytes) & (uint)mask) == bucket) nodes[found++] = node;
        }
        HashKeyedNodeStorage storage = new(nodes.AsSpan(0, count));

        for (int phase = 0; phase < 2; phase++)
        {
            for (int i = 0; i < count; i++)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(storage.Get(null, TreePath.Empty, HashOf(nodes[i])), Is.EqualTo(nodes[i]));
                    Assert.That(storage.KeyExists(null, TreePath.Empty, HashOf(nodes[i])), Is.True);
                }
            }
            Assert.That(storage.Get(null, TreePath.Empty, HashOf(nodes[^1])), Is.Null);
            storage.Set(null, TreePath.Empty, UnknownHash, [0x01]);
        }
        storage.Set(null, TreePath.Empty, HashOf(nodes[0]), null);
        Assert.That(storage.Get(null, TreePath.Empty, HashOf(nodes[0])), Is.Null);
        Assert.That(storage.KeyExists(null, TreePath.Empty, HashOf(nodes[0])), Is.False);
    }

    [Test]
    public void Duplicate_witness_nodes_resolve_and_can_be_evicted([Values(1, 9)] int count)
    {
        byte[][] nodes = new byte[count][];
        Array.Fill(nodes, Nodes[1]);
        HashKeyedNodeStorage storage = Storage(nodes);
        ValueHash256 hash = HashOf(Nodes[1]);
        Assert.That(storage.Get(null, TreePath.Empty, hash), Is.EqualTo(Nodes[1]));
        storage.Set(null, TreePath.Empty, hash, null);
        Assert.That(storage.Get(null, TreePath.Empty, hash), Is.Null);
    }

    [Test]
    public void Host_witness_storage_supports_concurrent_reads_and_writes()
    {
        const int NodeCount = 4096;
        byte[][] nodes = new byte[NodeCount][];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = [(byte)i, (byte)(i >> 8), 0xa5];

        using IOwnedReadOnlyList<byte[]> witness = nodes.ToPooledList();
        INodeStorage storage = WitnessNodeStorage.Create(witness);
        ValueHash256[] hashes = Array.ConvertAll(nodes, HashOf);

        Parallel.For(0, nodes.Length, i =>
        {
            using INodeStorage.IWriteBatch batch = storage.StartWriteBatch();
            _ = storage.Get(null, TreePath.Empty, hashes[i]);
            batch.Set(null, TreePath.Empty, hashes[i], null, WriteFlags.None);
            _ = storage.KeyExists(null, TreePath.Empty, hashes[i]);
        });

        for (int i = 0; i < nodes.Length; i++)
        {
            Assert.That(storage.Get(null, TreePath.Empty, hashes[i]), Is.Null);
            Assert.That(storage.KeyExists(null, TreePath.Empty, hashes[i]), Is.False);
        }

        Parallel.For(0, nodes.Length, i =>
        {
            using INodeStorage.IWriteBatch batch = storage.StartWriteBatch();
            batch.Set(null, TreePath.Empty, hashes[i], nodes[i], WriteFlags.None);
            _ = storage.Get(null, TreePath.Empty, hashes[i]);
            _ = storage.KeyExists(null, TreePath.Empty, hashes[i]);
        });

        for (int i = 0; i < nodes.Length; i++)
        {
            Assert.That(storage.Get(null, TreePath.Empty, hashes[i]), Is.EqualTo(nodes[i]));
            Assert.That(storage.KeyExists(null, TreePath.Empty, hashes[i]), Is.True);
        }
    }

    private static void Write(HashKeyedNodeStorage storage, bool throughBatch, in ValueHash256 hash, byte[] data)
    {
        if (throughBatch)
        {
            using INodeStorage.IWriteBatch batch = storage.StartWriteBatch();
            batch.Set(null, TreePath.Empty, hash, data, WriteFlags.None);
        }
        else
        {
            storage.Set(null, TreePath.Empty, hash, data);
        }
    }

    /// <summary>
    /// Finds two keys the store buckets together that differ in <paramref name="half"/> alone - one
    /// 4-byte half of one of the four words equality compares - so that word's comparison is what
    /// tells them apart, and only the half of it that varies can do so.
    /// </summary>
    /// <remarks>
    /// Searched rather than hard-coded: the hash code is seeded, so no fixed pair collides across runs.
    /// One half rather than one whole word, because a pair differing across the whole word is separated
    /// by any partial read of it: for word <c>w</c>, dropping its comparison fails halves <c>2w</c> and
    /// <c>2w + 1</c>, while narrowing it to the low or high 32 bits fails the half it stops reading -
    /// <c>2w + 1</c> and <c>2w</c> respectively. A pair differing anywhere else would still be separated
    /// by the words that remain. A 32-bit birthday collision is overwhelmingly likely well inside
    /// <c>Attempts</c>.
    /// <para>
    /// <see cref="NodeKeyHashCode"/> mirrors the store's private hash. Should the two ever drift apart,
    /// the pair no longer shares a bucket and this test passes without reaching equality at all, rather
    /// than failing.
    /// </para>
    /// </remarks>
    private static (ValueHash256, ValueHash256) FindHashCodeCollision(int half)
    {
        const int Attempts = 1 << 19;
        Dictionary<int, ValueHash256> seen = [];

        for (int i = 0; i < Attempts; i++)
        {
            ValueHash256 candidate = default;
            BinaryPrimitives.WriteInt32LittleEndian(candidate.BytesAsSpan[(half * sizeof(uint))..], i);
            int hashCode = NodeKeyHashCode(in candidate);

            // Distinct i give distinct candidates, so the first repeated hash code is the collision.
            if (seen.TryGetValue(hashCode, out ValueHash256 previous)) return (previous, candidate);

            seen[hashCode] = candidate;
        }

        Assert.Fail($"No hash-code collision within {Attempts} keys.");
        return default;
    }

    /// <summary>Mirrors the store's private key hash so the search targets the same buckets.</summary>
    private static int NodeKeyHashCode(in ValueHash256 hash) =>
        (int)SpanExtensions.FastHash64For32Bytes(ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in hash)));
}
