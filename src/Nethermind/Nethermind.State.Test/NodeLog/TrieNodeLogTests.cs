// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
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
    internal const int LiveGenerations = 8;
    internal const int CompactionTimeoutMilliseconds = 10_000;
    internal const int PollingMilliseconds = 10;

    [Test]
    public void Log_buffers_are_zero_filled([Values(0, 1, GenerationBytes, 2 * 1024 * 1024 - 1, 2 * 1024 * 1024, 2 * 1024 * 1024 + 1)] int length)
    {
        byte[] buffer = LogBuffer.Allocate(length);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.Length, Is.EqualTo(length));
            Assert.That(buffer.AsSpan().IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
        }
    }

    [Test]
    [Platform("Linux")]
    public void Large_generation_buffers_stay_pinned_during_compacting_collections([Values(2 * 1024 * 1024, 4 * 1024 * 1024 + 1)] int length)
    {
        TrieNodeLog log = new(0, length);
        log.Reserve(NodeRecord.RecordAlignment, out byte[] buffer, out _);
        IntPtr address = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);
        buffer[0] = 1;
        buffer[^1] = 2;

        GCLargeObjectHeapCompactionMode previous = GCSettings.LargeObjectHeapCompactionMode;
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        finally
        {
            GCSettings.LargeObjectHeapCompactionMode = previous;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0), Is.EqualTo(address));
            Assert.That(buffer[0], Is.EqualTo(1));
            Assert.That(buffer[^1], Is.EqualTo(2));
        }
    }

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
            Assert.That(() => log.FindRoot(address, Root(-2)), Is.Zero.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "a root whose record dropped");
            Assert.That(() => log.FindRoot(address, roots[0]), Is.Zero.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "a root whose root key dropped");
            Assert.That(Metrics.TrieNodeLogRootMisses, Is.GreaterThanOrEqualTo(missesBefore + 2), "misses are counted");
        }
    }

    [Test]
    public void Dropped_generation_keeps_last_record_per_path_not_rewritten_later([Values(0UL, 1UL << 20)] ulong compressedBudget)
    {
        const ulong scenarioGeneration = 32;
        TrieNodeLog log = new(compressedBudget, GenerationBytes);
        List<(ulong Pointer, byte[] Rlp)> unique = [];
        AppendUniqueUntil(scenarioGeneration);
        (ulong Pointer, byte[] Rlp) superseded = AppendRecord(TestItem.KeccakA, Leaf(-1, 8));
        (ulong Pointer, byte[] Rlp) kept = AppendRecord(TestItem.KeccakA, Leaf(-2, 8));
        (ulong Pointer, byte[] Rlp) rewritten = AppendRecord(TestItem.KeccakB, Leaf(-3, 8));
        log.SetRoot(null, Root(-1), kept.Pointer);
        AppendUniqueUntil(scenarioGeneration + 1);
        (ulong Pointer, byte[] Rlp) rewrite = AppendRecord(TestItem.KeccakB, Leaf(-4, 8));
        AppendUniqueUntil(scenarioGeneration + LiveGenerations);

        bool keeps = compressedBudget > 0;
        // The scenario generation is the newest dropped one and generations are compacted and evicted oldest first.
        Assert.That(() => (log.TryLocate(superseded.Pointer, out _), log.FindRoot(null, Root(-1))), Is.EqualTo((false, keeps ? kept.Pointer : 0UL)).After(CompactionTimeoutMilliseconds, PollingMilliseconds),
            "the scenario generation is compacted, keeping its root key only within the budget");
        ulong oldestResolving = unique.Where(record => Resolves(log, record)).Min(record => GenerationOf(record.Pointer));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new[] { superseded, kept, rewritten }.Select(record => GenerationOf(record.Pointer)), Is.All.EqualTo(scenarioGeneration), "scenario generation");
            Assert.That(Resolves(log, kept), Is.EqualTo(keeps), "the last record of a path not written later");
            Assert.That(Resolves(log, rewritten), Is.False, "a record of a path written later");
            Assert.That(Resolves(log, rewrite), Is.True, "a live record");
            Assert.That(unique.Select(record => Resolves(log, record)), Is.EqualTo(unique.Select(record => GenerationOf(record.Pointer) >= oldestResolving)), "compressed generations are evicted oldest first");
            Assert.That(oldestResolving, keeps ? Is.InRange(2UL, scenarioGeneration) : Is.EqualTo(scenarioGeneration + 1), "oldest generation still resolving");
        }

        (ulong Pointer, byte[] Rlp) AppendRecord(Hash256 address, byte[] rlp) => (AppendAt(log, address, rlp), rlp);

        void AppendUniqueUntil(ulong generation)
        {
            while (unique.Count == 0 || GenerationOf(unique[^1].Pointer) < generation)
            {
                unique.Add(AppendRecord(RecordAddress(unique.Count), Leaf(unique.Count, 1000)));
            }
        }
    }

    [Test]
    public void Concurrent_reservations_and_root_sets_are_disjoint_and_readable([Values] bool compacting)
    {
        const int threadCount = 16;
        const int recordsPerThread = 10_000;
        const int recordsPerRetain = 1_000;
        TrieNodeLog log = new(1 << 20, GenerationBytes);
        using TrieNodeLogRetain retain = compacting ? null : log.RetainActive();
        ulong[][] pointers = new ulong[threadCount][];
        TrieNodeLogRetain[] threadRetains = new TrieNodeLogRetain[threadCount];

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, thread =>
        {
            pointers[thread] = new ulong[recordsPerThread];
            for (int i = 0; i < recordsPerThread; i++)
            {
                // Like a scope per block, a writer retains a generation no newer than those it writes.
                if (i % recordsPerRetain == 0)
                {
                    threadRetains[thread]?.Dispose();
                    threadRetains[thread] = log.RetainActive();
                }

                int seed = thread * recordsPerThread + i;
                pointers[thread][i] = AppendAt(log, RecordAddress(seed), Leaf(seed, 8));
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
                bool retained = !compacting || i >= recordsPerThread - recordsPerRetain;
                bool located = log.TryLocate(pointer, out NodeRecordRef record);
                if (located ? !Matches(record, Leaf(seed, 8)) : retained) failures.Add($"record {seed}");
                ulong root = log.FindRoot(ConcurrentAddress(thread), Root(seed));
                if (root != pointer && (retained || root != 0)) failures.Add($"root {seed}");
            }
        }

        foreach (TrieNodeLogRetain threadRetain in threadRetains) threadRetain.Dispose();
        Assert.That(failures, Is.Empty);
    }

    private static Hash256 SameSlotAddress(Hash256 address)
    {
        byte[] bytes = address.BytesToArray();
        bytes[^1] ^= 1;
        return new Hash256(bytes);
    }

    private static Hash256 ConcurrentAddress(int thread) => thread % 2 == 0 ? null : Keccak.Compute(BitConverter.GetBytes(thread));

    internal static ulong GenerationOf(ulong pointer) => pointer >> 32;

    private static ValueHash256 Root(int seed) => ValueKeccak.Compute(BitConverter.GetBytes(seed));

    private static Hash256 RecordAddress(int seed) => Keccak.Compute(BitConverter.GetBytes(seed));

    private static ulong Append(TrieNodeLog log, byte[] rlp) => AppendAt(log, null, rlp);

    private static ulong AppendAt(TrieNodeLog log, Hash256 address, byte[] rlp) => log.AppendRecordFromRlp(address, TreePath.Empty, ValueKeccak.Compute(rlp), rlp);

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
        log.TryLocate(record.Pointer, out NodeRecordRef located) && Matches(located, record.Rlp);

    private static bool Matches(NodeRecordRef record, byte[] rlp) => record.Rlp.SequenceEqual(rlp) && record.Hash == ValueKeccak.Compute(rlp);

    /// <remarks>Dropped generations stay readable in place until they are compacted in the background.</remarks>
    private static void AssertResolvesFrom(TrieNodeLog log, List<(ulong Pointer, byte[] Rlp)> records, ulong oldestReadable, string message) =>
        Assert.That(() => records.Select(record => Resolves(log, record)).ToArray(), Is.EqualTo(records.Select(record => GenerationOf(record.Pointer) >= oldestReadable)).After(CompactionTimeoutMilliseconds, PollingMilliseconds), message);
}
