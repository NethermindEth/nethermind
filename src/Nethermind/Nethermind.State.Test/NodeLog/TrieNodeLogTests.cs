// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;
using NUnit.Framework;

namespace Nethermind.Store.Test.NodeLog;

internal class TrieNodeLogTests
{
    private const int GenerationBytes = 64 * 1024;
    private const int LiveGenerations = 8;

    [Test]
    public void Records_resolve_in_place_until_their_generation_drops()
    {
        TrieNodeLog log = new(0, GenerationBytes);
        TrieNodeLogRetain first = log.RetainActive();
        TrieNodeLogRetain middle = null;
        TrieNodeLogRetain middleCopy = null;
        List<(ulong Pointer, byte[] Rlp)> records = [];
        while (records.Count == 0 || GenerationOf(records[^1].Pointer) < 24)
        {
            if (middle is null && records.Count > 0 && GenerationOf(records[^1].Pointer) == 6)
            {
                middle = log.RetainActive();
                middleCopy = middle.Copy();
            }

            byte[] rlp = Leaf(records.Count, 1000);
            records.Add((Append(log, rlp), rlp));
        }

        ulong activeGeneration = GenerationOf(records[^1].Pointer);
        Assert.That(records.Select(record => Resolves(log, record)), Is.All.True, "everything resolves while the first generation is retained");

        middle.Dispose();
        middle.Dispose();
        first.Dispose();
        AssertResolvesFrom(log, records, middleCopy.Generation, "a copy keeps its generation and every later one after the original is disposed");
        Assert.That(() => log.GetRecord(records[0].Pointer), Throws.InstanceOf<TrieException>());

        middleCopy.Dispose();
        AssertResolvesFrom(log, records, activeGeneration - LiveGenerations + 1, "only the live generations resolve after the last release");

        byte[] oversize = Leaf(-1, GenerationBytes + 1);
        ulong oversizePointer = Append(log, oversize);
        ulong nextPointer = Append(log, Leaf(-2, 1000));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.TryLocate(0, out _), Is.False, "pointer 0");
            Assert.That(log.TryLocate((activeGeneration + 100) << 32, out _), Is.False, "unknown generation");
            Assert.That(Resolves(log, (oversizePointer, oversize)), Is.True, "oversize record");
            Assert.That(oversizePointer, Is.EqualTo((activeGeneration + 1) << 32), "oversize record starts its own generation");
            Assert.That(GenerationOf(nextPointer), Is.EqualTo(activeGeneration + 2), "the record after an oversize one");
        }
    }

    [Test]
    public void Root_index_returns_the_newest_matching_root([Values] bool storage)
    {
        TrieNodeLog log = new(0, GenerationBytes);
        Hash256 address = storage ? TestItem.KeccakA : null;
        TrieNodeLogRetain retain = log.RetainActive();
        ulong droppedPointer = Append(log, Leaf(-1, 8));

        ValueHash256[] roots = new ValueHash256[1500];
        ulong[] pointers = new ulong[roots.Length];
        for (int i = 0; i < roots.Length; i++)
        {
            roots[i] = Root(i);
            pointers[i] = Append(log, Leaf(i, 8));
            log.SetRoot(address, roots[i], pointers[i]);
        }

        Hash256 otherAddress = storage ? SameSlotAddress(address) : Hash256.Zero;
        ulong otherAddressPointer = Append(log, Leaf(-2, 8));
        log.SetRoot(otherAddress, roots[0], otherAddressPointer);
        pointers[1] = Append(log, Leaf(-3, 8));
        log.SetRoot(address, roots[1], pointers[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roots.Select(root => log.FindRoot(address, root)), Is.EqualTo(pointers), "newest pointer of every root");
            Assert.That(log.FindRoot(otherAddress, roots[0]), Is.EqualTo(otherAddressPointer), "the same root under another address");
            Assert.That(log.FindRoot(address, Root(-1)), Is.Zero, "a root never set");
        }

        retain.Dispose();
        while (GenerationOf(Append(log, Leaf(0, 1000))) < GenerationOf(droppedPointer) + LiveGenerations) { }

        long missesBefore = Metrics.TrieNodeLogRootMisses;
        log.SetRoot(address, Root(-2), droppedPointer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.FindRoot(address, Root(-2)), Is.Zero, "a root whose record dropped");
            Assert.That(log.FindRoot(address, roots[0]), Is.Zero, "a root whose root key dropped");
            Assert.That(Metrics.TrieNodeLogRootMisses, Is.GreaterThanOrEqualTo(missesBefore + 2), "misses are counted");
        }
    }

    [Test]
    public void Concurrent_reservations_and_root_sets_are_disjoint_and_readable()
    {
        const int threadCount = 16;
        const int recordsPerThread = 10_000;
        TrieNodeLog log = new(0, GenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        ulong[][] pointers = new ulong[threadCount][];

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, thread =>
        {
            pointers[thread] = new ulong[recordsPerThread];
            for (int i = 0; i < recordsPerThread; i++)
            {
                int seed = thread * recordsPerThread + i;
                pointers[thread][i] = Append(log, Leaf(seed, 8));
                log.SetRoot(ConcurrentAddress(thread), Root(seed), pointers[thread][i]);
            }
        });

        List<string> failures = [];
        for (int thread = 0; thread < threadCount; thread++)
        {
            for (int i = 0; i < recordsPerThread; i++)
            {
                int seed = thread * recordsPerThread + i;
                ulong pointer = pointers[thread][i];
                if (!Resolves(log, (pointer, Leaf(seed, 8)))) failures.Add($"record {seed}");
                if (log.FindRoot(ConcurrentAddress(thread), Root(seed)) != pointer) failures.Add($"root {seed}");
            }
        }

        Assert.That(failures, Is.Empty);
    }

    private static Hash256 SameSlotAddress(Hash256 address)
    {
        byte[] bytes = address.BytesToArray();
        bytes[^1] ^= 1;
        return new Hash256(bytes);
    }

    private static Hash256 ConcurrentAddress(int thread) => thread % 2 == 0 ? null : Keccak.Compute(BitConverter.GetBytes(thread));

    private static ulong GenerationOf(ulong pointer) => pointer >> 32;

    private static ValueHash256 Root(int seed) => ValueKeccak.Compute(BitConverter.GetBytes(seed));

    private static ulong Append(TrieNodeLog log, byte[] rlp) => log.AppendRecordFromRlp(null, TreePath.Empty, ValueKeccak.Compute(rlp), rlp);

    internal static byte[] Leaf(int seed, int valueLength)
    {
        byte[] value = new byte[valueLength];
        BinaryPrimitives.WriteInt32LittleEndian(value, seed);
        byte[] keyNibbles = [1, 2];
        byte[] rlp = new byte[NodeRlp.LeafLength(keyNibbles, value)];
        NodeRlp.WriteLeaf(keyNibbles, value, rlp);
        return rlp;
    }

    private static bool Resolves(TrieNodeLog log, (ulong Pointer, byte[] Rlp) record) =>
        log.TryLocate(record.Pointer, out NodeRecordRef located)
        && located.Rlp.SequenceEqual(record.Rlp)
        && located.Hash == ValueKeccak.Compute(record.Rlp);

    private static void AssertResolvesFrom(TrieNodeLog log, List<(ulong Pointer, byte[] Rlp)> records, ulong oldestReadable, string message) =>
        Assert.That(records.Select(record => Resolves(log, record)), Is.EqualTo(records.Select(record => GenerationOf(record.Pointer) >= oldestReadable)), message);
}
