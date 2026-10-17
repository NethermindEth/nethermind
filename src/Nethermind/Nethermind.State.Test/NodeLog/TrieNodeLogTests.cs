// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
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
    public void Generation_ids_stop_before_the_transient_namespace()
    {
        ulong last = TrieNodeLog.NextGeneration(NodeRecord.TransientGenerationMask - 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(last, Is.EqualTo(0x7fffffffUL));
            Assert.That(() => TrieNodeLog.NextGeneration(last), Throws.TypeOf<TrieException>());
            Assert.That(() => TrieNodeLog.NextGeneration(ulong.MaxValue), Throws.TypeOf<TrieException>(), "cannot wrap");
        }
    }

    [Test]
    public void Real_log_rejects_transient_pointers()
    {
        using TrieNodeLog log = new(0, GenerationBytes);
        byte[] rlp = Leaf(1, 8);
        ulong realPointer = Append(log, rlp);
        WarmedNodes warmed = new(1);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        warmed.TryInsert(null, TreePath.Empty, hash, rlp, out _, out ulong temporaryPointer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.TryLocate(temporaryPointer, out _), Is.False);
            Assert.That(log.TryLocate(realPointer | NodeRecord.TransientPointerMask, out _), Is.False);
            Assert.That(log.TryLocate(realPointer, out _), Is.True);
            Assert.That(log.TryGetNode(temporaryPointer, TreePath.Empty, out _), Is.False);
            Assert.That(log.TryGetNodePointer(realPointer | NodeRecord.TransientPointerMask, TreePath.Empty, out _), Is.False);
            Assert.That(log.EnumerateNodes(temporaryPointer), Is.Empty);
            Assert.That(log.EnumerateNodes(realPointer | NodeRecord.TransientPointerMask), Is.Empty);
            Assert.That(warmed.TryGet(realPointer, hash, out _), Is.False);
            Assert.That(NodeRecord.PersistentPointer(temporaryPointer), Is.Zero);
            Assert.That(NodeRecord.PersistentPointer(realPointer), Is.EqualTo(realPointer));
        }
    }

    [Test]
    public void Log_buffers_are_zero_filled([Values(0, 1, GenerationBytes, 2 * 1024 * 1024 - 1, 2 * 1024 * 1024, 2 * 1024 * 1024 + 1)] int length)
    {
        using LogBuffer buffer = LogBuffer.Allocate(length);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.Length, Is.EqualTo(length));
            Assert.That(buffer.AsSpan().IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
        }
    }

    [Test]
    public void Generation_blooms_have_at_least_one_key_of_capacity([Values(0, 1, 127, 128, GenerationBytes)] int generationBytes)
    {
        using TrieNodeLog log = new(0, generationBytes);
        using TrieNodeLog.ReadLease lease = log.BeginRead();
        ulong pointer = log.Reserve(0, out _, out _);
        BloomFilter bloom = log.FindBloom(pointer)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bloom.Capacity, Is.EqualTo(Math.Max(1, generationBytes / 128)));
            Assert.That(bloom.BitsPerKey, Is.EqualTo(14));
            Assert.That(bloom.MightContain(0), Is.False);
        }
    }

    [Test]
    public void Generation_buffers_stay_at_the_same_native_address_during_compacting_collections([Values(2 * 1024 * 1024, 4 * 1024 * 1024 + 1)] int length)
    {
        using TrieNodeLog log = new(0, length);
        log.Reserve(NodeRecord.RecordAlignment, out LogBuffer buffer, out _);
        ref byte address = ref MemoryMarshal.GetReference(buffer.AsSpan());
        buffer.AsSpan()[0] = 1;
        buffer.AsSpan()[^1] = 2;

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
            Assert.That(Unsafe.AreSame(ref address, ref MemoryMarshal.GetReference(buffer.AsSpan())), Is.True);
            Assert.That(buffer.AsSpan()[0], Is.EqualTo(1));
            Assert.That(buffer.AsSpan()[^1], Is.EqualTo(2));
        }
    }

    [Test]
    public void Retains_capture_exact_allocation_boundaries_and_copies_preserve_them()
    {
        using TrieNodeLog log = new(0, GenerationBytes);
        using TrieNodeLogRetain first = log.RetainActive();
        ulong firstPointer = log.Reserve(NodeRecord.RecordAlignment, out _, out _);
        using TrieNodeLogRetain second = log.RetainActive();
        ulong secondPointer = log.Reserve(NodeRecord.RecordAlignment, out _, out _);
        using TrieNodeLogRetain copy = first.Copy();
        log.Reserve(GenerationBytes, out _, out _);
        log.Reserve(NodeRecord.RecordAlignment, out _, out _);
        using TrieNodeLogRetain rotated = log.RetainActive();
        ulong rotatedPointer = log.Reserve(NodeRecord.RecordAlignment, out _, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.WriteBoundary, Is.EqualTo(firstPointer));
            Assert.That(second.Generation, Is.EqualTo(first.Generation));
            Assert.That(second.WriteBoundary, Is.EqualTo(secondPointer));
            Assert.That(copy.WriteBoundary, Is.EqualTo(first.WriteBoundary));
            Assert.That(rotated.Generation, Is.GreaterThan(first.Generation));
            Assert.That(rotated.WriteBoundary, Is.EqualTo(rotatedPointer));
        }
    }

    [Test]
    public void Retain_boundary_clamps_failed_reservation_overshoot([Values(GenerationBytes + 8L, (1L << 32) + 8)] long used)
    {
        using TrieNodeLog log = new(0, GenerationBytes);
        object active = typeof(TrieNodeLog).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
        active.GetType().GetField("Used")!.SetValue(active, used);
        using TrieNodeLogRetain retain = log.RetainActive();
        ulong pointer = log.Reserve(NodeRecord.RecordAlignment, out _, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retain.WriteBoundary, Is.EqualTo(retain.Generation << TrieNodeLog.OffsetBits | GenerationBytes));
            Assert.That(pointer, Is.GreaterThan(retain.WriteBoundary));
        }
    }

    [Test]
    public void Concurrent_reservations_never_precede_their_retain_boundary()
    {
        using TrieNodeLog log = new(0, GenerationBytes);
        Parallel.For(0, 16, thread =>
        {
            for (int i = 0; i < 1024; i++)
            {
                using TrieNodeLogRetain retain = log.RetainActive();
                ulong pointer = log.Reserve(NodeRecord.RecordAlignment, out _, out _);
                Assert.That(pointer, Is.GreaterThanOrEqualTo(retain.WriteBoundary));
            }
        });
    }

    [Test]
    public void Native_buffer_disposal_is_idempotent()
    {
        using LogBuffer buffer = LogBuffer.Allocate(1);
        ((IDisposable)buffer).Dispose();
        ((IDisposable)buffer).Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.IsDisposed, Is.True);
            Assert.That(() => buffer.AsSpan().ToArray(), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void Reader_keeps_retired_records_and_pending_leaf_values_alive([Values] bool compressed)
    {
        const int generationBytes = 4096;
        using TrieNodeLog log = new(2048, generationBytes);
        log.Reserve(0, out LogBuffer original, out _);
        byte[] rlp = Leaf(1, 1000);
        ulong pointer = AppendAt(log, TestItem.KeccakA, rlp);
        BloomFilter bloom = log.FindBloom(pointer)!;
        ulong nodeKey = NodeRecord.NodeKey(TestItem.KeccakA, TreePath.Empty);
        if (compressed)
        {
            AdvanceTo(LiveGenerations + 1);
            Assert.That(() => original.IsDisposed, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        }

        NodeRecordRef record;
        ValueRef pendingValue;
        using (log.BeginRead())
        {
            Assert.That(log.TryLocate(pointer, out record), Is.True);
            pendingValue = record.LeafValue;
            byte[] expectedValue = pendingValue.Span.ToArray();
            if (compressed)
            {
                ulong later = AppendAt(log, TestItem.KeccakB, Leaf(2, 1000));
                AdvanceTo(GenerationOf(later) + LiveGenerations);
                Assert.That(() => log.FindBloom(later) is null, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
                using (log.BeginRead()) Assert.That(log.TryLocate(pointer, out _), Is.True, "retired compressed lookup is protected by the reader");
            }
            else
            {
                AdvanceTo(LiveGenerations + 1);
                Assert.That(() => log.TryLocate(pointer, out NodeRecordRef current) && !current.Span.Overlaps(record.Span),
                    Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "the original buffer has been replaced by compressed storage");
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(original.IsDisposed, Is.False);
                    Assert.That(log.FindBloom(pointer), Is.Null, "the compacted generation no longer exposes its bloom");
                    Assert.That(bloom.MightContain(nodeKey), Is.True, "the reader still borrows the retired bloom");
                }
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(record.Rlp.ToArray(), Is.EqualTo(rlp));
                Assert.That(pendingValue.Span.ToArray(), Is.EqualTo(expectedValue), "a pending bulk-set value borrows the same retired storage");
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => record.Rlp.ToArray(), Throws.InstanceOf<ObjectDisposedException>(), "the final reader deterministically freed its retired native owner");
            Assert.That(() => pendingValue.Span.ToArray(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(original.IsDisposed, Is.True);
            Assert.That(() => bloom.MightContain(nodeKey), Throws.InstanceOf<ObjectDisposedException>());
        }

        void AdvanceTo(ulong generation)
        {
            while (GenerationOf(log.Reserve(generationBytes, out _, out _)) < generation) { }
        }
    }

    [Test]
    public void Overlapping_readers_keep_compacted_away_records_lookupable_until_the_last_reader_finishes()
    {
        using TrieNodeLog log = new(1UL << 20, GenerationBytes);
        log.Reserve(0, out LogBuffer original, out _);
        byte[] previous = Leaf(1, 8);
        ulong pointer = Append(log, previous);
        AppendAt(log, TestItem.KeccakA, Leaf(3, 8));
        log.Reserve(GenerationBytes, out _, out _);
        Append(log, Leaf(2, 8));
        TrieNodeLog.ReadLease first = log.BeginRead();
        TrieNodeLog.ReadLease second = log.BeginRead();
        bool firstReleased = false;
        try
        {
            while (GenerationOf(log.Reserve(GenerationBytes, out _, out _)) < LiveGenerations + 1) { }
            Assert.That(() => log.FindBloom(pointer) is null, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
            using (log.BeginRead()) Assert.That(log.GetRecord(pointer).Rlp.ToArray(), Is.EqualTo(previous), "raw records omitted by compaction stay lookupable");
            first.Dispose();
            firstReleased = true;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(log.GetRecord(pointer).Rlp.ToArray(), Is.EqualTo(previous), "the overlapping reader still owns the retired generation");
                Assert.That(original.IsDisposed, Is.False);
            }
        }
        finally
        {
            if (!firstReleased) first.Dispose();
            second.Dispose();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(original.IsDisposed, Is.True);
            Assert.That(IsLocatable(log, pointer), Is.False, "a new reader cannot recover records evicted before it began");
        }
    }

    [Test]
    public void Retired_lookup_uses_full_generation_ids_when_the_ring_slot_is_reused()
    {
        const int generationBytes = 256;
        using TrieNodeLog log = new(0, generationBytes);
        byte[] previous = Leaf(1, 8);
        byte[] current = Leaf(2, 8);
        ulong pointer = Append(log, previous);
        ulong replacement;
        using (log.BeginRead())
        {
            AdvanceTo(LiveGenerations + 1);
            Assert.That(() => log.FindBloom(pointer) is null, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
            // The first slot is clear before generation 4097 reuses it.
            using (log.BeginRead()) { }
            AdvanceTo(4096);
            replacement = Append(log, current);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(GenerationOf(replacement), Is.EqualTo(GenerationOf(pointer) + 4096));
                Assert.That((uint)replacement, Is.EqualTo((uint)pointer));
                Assert.That(log.GetRecord(pointer).Rlp.ToArray(), Is.EqualTo(previous));
                Assert.That(log.GetRecord(replacement).Rlp.ToArray(), Is.EqualTo(current));
                Assert.That(log.TryLocate(pointer + (8192UL << 32), out _), Is.False, "same slot and offset do not match another full generation id");
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsLocatable(log, pointer), Is.False);
            Assert.That(IsLocatable(log, replacement), Is.True);
        }

        void AdvanceTo(ulong generation)
        {
            while (GenerationOf(log.Reserve(generationBytes, out _, out _)) < generation) { }
        }
    }

    [Test]
    public void Shutdown_defers_free_until_readers_and_retains_finish([Values] bool readerFinishesFirst)
    {
        using TrieNodeLog log = new(0, GenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        ulong pointer = Append(log, Leaf(1, 8));
        NodeRecordRef record = log.GetRecord(pointer);
        BloomFilter bloom = log.FindBloom(pointer)!;
        ulong nodeKey = NodeRecord.NodeKey(null, TreePath.Empty);
        log.Reserve(0, out LogBuffer buffer, out _);
        TrieNodeLog.ReadLease reader = log.BeginRead();
        log.Dispose();
        log.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.IsDisposed, Is.False);
            Assert.That(bloom.MightContain(nodeKey), Is.True);
            Assert.That(Matches(record, Leaf(1, 8)), Is.True);
            Assert.That(() => log.RetainActive(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => retain.Copy(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => log.GetRecord(pointer), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => log.Reserve(8, out _, out _), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => LogBulkSet.TryGet(log, null, null, TrieRoot.Empty, default), Throws.InstanceOf<ObjectDisposedException>());
        }

        if (readerFinishesFirst) reader.Dispose();
        else retain.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.IsDisposed, Is.False, "the other owner still borrows native memory");
            Assert.That(bloom.MightContain(nodeKey), Is.True);
            Assert.That(Matches(record, Leaf(1, 8)), Is.True);
        }
        if (readerFinishesFirst) retain.Dispose();
        else reader.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.IsDisposed, Is.True);
            Assert.That(() => bloom.MightContain(nodeKey), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void Shutdown_releases_storage_when_queued_compaction_finishes()
    {
        using TrieNodeLog log = new(0, GenerationBytes);
        ulong pointer = log.Reserve(GenerationBytes, out LogBuffer first, out _);
        BloomFilter bloom = log.FindBloom(pointer)!;
        while (GenerationOf(log.Reserve(GenerationBytes, out _, out _)) < LiveGenerations + 1) { }
        log.Dispose();
        Assert.That(() => first.IsDisposed, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        // Wait for the disposal lock to cover the rest of this generation's cleanup, too.
        log.Dispose();
        Assert.That(() => bloom.MightContain(0), Throws.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void Compaction_failure_is_reported_and_does_not_prevent_shutdown_cleanup()
    {
        using TrieNodeLog log = new(0, GenerationBytes);
        ulong pointer = log.Reserve(GenerationBytes, out LogBuffer malformed, out _);
        BloomFilter bloom = log.FindBloom(pointer)!;
        malformed.AsSpan()[0] = (byte)NodeRecordKind.Leaf;
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(NodeRecord.RlpLengthOffset), GenerationBytes);
        while (GenerationOf(log.Reserve(GenerationBytes, out _, out _)) < LiveGenerations + 1) { }
        Assert.That(CompactionFailed, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        log.Dispose();
        Assert.That(() => malformed.IsDisposed, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        log.Dispose();
        Assert.That(() => bloom.MightContain(0), Throws.InstanceOf<ObjectDisposedException>());

        bool CompactionFailed()
        {
            try
            {
                using TrieNodeLog.ReadLease lease = log.BeginRead();
                return false;
            }
            catch (TrieException exception) when (exception.Message == "Trie node log compaction failed.")
            {
                return true;
            }
        }
    }

    [Test]
    public void Records_resolve_in_place_until_their_generation_drops()
    {
        using TrieNodeLog log = new(0, GenerationBytes);
        using TrieNodeLogRetain first = log.RetainActive();
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
    public void Retain_copy_cannot_resurrect_a_generation_dropped_by_a_concurrent_release()
    {
        using TrieNodeLog log = new(0, GenerationBytes);
        using TrieNodeLogRetain retain = log.RetainActive();
        while (GenerationOf(log.Reserve(GenerationBytes, out _, out _)) < LiveGenerations + 1) { }
        retain.Dispose();

        // Copy may have passed its disposed check before the releasing thread acquired the log lock.
        Assert.That(() => log.Retain(retain.Generation), Throws.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void Root_index_returns_the_newest_matching_root([Values] bool storage)
    {
        using TrieNodeLog log = new(0, GenerationBytes);
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
        using TrieNodeLog log = new(compressedBudget, GenerationBytes);
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
        Assert.That(() => (IsLocatable(log, superseded.Pointer), log.FindRoot(null, Root(-1))), Is.EqualTo((false, keeps ? kept.Pointer : 0UL)).After(CompactionTimeoutMilliseconds, PollingMilliseconds),
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
        using TrieNodeLog log = new(1 << 20, GenerationBytes);
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
                using TrieNodeLog.ReadLease lease = log.BeginRead();
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

    internal static bool IsLocatable(TrieNodeLog log, ulong pointer)
    {
        using TrieNodeLog.ReadLease lease = log.BeginRead();
        return log.TryLocate(pointer, out _);
    }

    private static bool Resolves(TrieNodeLog log, (ulong Pointer, byte[] Rlp) record)
    {
        using TrieNodeLog.ReadLease lease = log.BeginRead();
        return log.TryLocate(record.Pointer, out NodeRecordRef located) && Matches(located, record.Rlp);
    }

    private static bool Matches(NodeRecordRef record, byte[] rlp) => record.Rlp.SequenceEqual(rlp) && record.Hash == ValueKeccak.Compute(rlp);

    /// <remarks>Dropped generations stay readable in place until they are compacted in the background.</remarks>
    private static void AssertResolvesFrom(TrieNodeLog log, List<(ulong Pointer, byte[] Rlp)> records, ulong oldestReadable, string message) =>
        Assert.That(() => records.Select(record => Resolves(log, record)).ToArray(), Is.EqualTo(records.Select(record => GenerationOf(record.Pointer) >= oldestReadable)).After(CompactionTimeoutMilliseconds, PollingMilliseconds), message);
}
