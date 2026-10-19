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
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.Store.Test.NodeBuffer;

internal class NodeRecordBufferTests
{
    private const int GenerationBytes = 64 * 1024;
    internal const int LiveGenerations = 8;
    internal const int CompactionTimeoutMilliseconds = 10_000;
    internal const int PollingMilliseconds = 10;

    [Test]
    public void Contract_write_batch_registers_every_record_on_dispose()
    {
        using NodeRecordBuffer implementation = new(0, GenerationBytes);
        INodeRecordBuffer buffer = implementation;
        using IDisposable pin = buffer.PinAll();
        buffer.AppendRecordFromRlp(null, TreePath.Empty, ValueKeccak.Compute(Leaf(0, 8)), Leaf(0, 8));
        byte[][] rlps = [Leaf(1, 8), Leaf(2, 8)];
        int recordLength = NodeRecordCodec.AlignedLength(NodeRecordCodec.Length(0, rlps[0].Length));
        ulong[] keys = [NodeRecordCodec.NodeKeyHash(null, TreePath.FromHexString("1")), NodeRecordCodec.NodeKeyHash(null, TreePath.FromHexString("2"))];
        INodeRecordWriteBatch batch = buffer.StartWriteBatch(recordLength * 2);
        ulong pointer = batch.BasePointer;
        int length = batch.Bytes.Length;
        for (int i = 0; i < rlps.Length; i++)
            NodeRecordCodec.WriteFromRlp(rlps[i], NodeRecordKind.Leaf, 0, ValueKeccak.Compute(rlps[i]), keys[i], batch.Bytes[(i * recordLength)..]);

        BloomFilter bloom = implementation.FindBloom(pointer)!;
        bool registeredEarly = keys.Any(key => bloom.MightContain(key));
        batch.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(length, Is.EqualTo(recordLength * 2));
            Assert.That(registeredEarly, Is.False, "records are registered on dispose");
            for (int i = 0; i < rlps.Length; i++)
            {
                Assert.That(bloom.MightContain(keys[i]), Is.True);
                Assert.That(buffer.GetRecord(pointer + (ulong)(i * recordLength)).Rlp.ToArray(), Is.EqualTo(rlps[i]));
            }
        }
    }

    [Test]
    public void Generation_ids_stop_before_the_transient_namespace()
    {
        ulong last = NodeRecordBuffer.NextGeneration(NodeRecordCodec.TransientGenerationMask - 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(last, Is.EqualTo(0x7fffffffUL));
            Assert.That(() => NodeRecordBuffer.NextGeneration(last), Throws.TypeOf<TrieException>());
            Assert.That(() => NodeRecordBuffer.NextGeneration(ulong.MaxValue), Throws.TypeOf<TrieException>(), "cannot wrap");
        }
    }

    [Test]
    public void Real_buffer_rejects_transient_pointers()
    {
        using NodeRecordBuffer buffer = new(0, GenerationBytes);
        byte[] rlp = Leaf(1, 8);
        ulong realPointer = Append(buffer, rlp);
        WarmedNodes warmed = new(1);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        warmed.TryInsert(null, TreePath.Empty, hash, rlp, out _, out ulong temporaryPointer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.TryLocate(temporaryPointer, out _), Is.False);
            Assert.That(buffer.TryLocate(realPointer | NodeRecordCodec.TransientPointerMask, out _), Is.False);
            Assert.That(buffer.TryLocate(realPointer, out _), Is.True);
            Assert.That(buffer.TryGetNode(temporaryPointer, TreePath.Empty, out _), Is.False);
            Assert.That(buffer.TryGetNodePointer(realPointer | NodeRecordCodec.TransientPointerMask, TreePath.Empty, out _), Is.False);
            Assert.That(buffer.EnumerateNodes(temporaryPointer), Is.Empty);
            Assert.That(buffer.EnumerateNodes(realPointer | NodeRecordCodec.TransientPointerMask), Is.Empty);
            Assert.That(warmed.TryGet(realPointer, hash, out _), Is.False);
            Assert.That(NodeRecordCodec.PersistentPointer(temporaryPointer), Is.Zero);
            Assert.That(NodeRecordCodec.PersistentPointer(realPointer), Is.EqualTo(realPointer));
        }
    }

    [Test]
    public void Native_buffers_are_zero_filled([Values(0, 1, GenerationBytes, 2 * 1024 * 1024 - 1, 2 * 1024 * 1024, 2 * 1024 * 1024 + 1)] int length)
    {
        using NativeNodeBuffer buffer = NativeNodeBuffer.Allocate(length);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.Length, Is.EqualTo(length));
            Assert.That(buffer.AsSpan().IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
        }
    }

    [Test]
    public void Generation_blooms_have_at_least_one_key_of_capacity([Values(0, 1, 127, 128, GenerationBytes)] int generationBytes)
    {
        using NodeRecordBuffer buffer = new(0, generationBytes);
        using IDisposable pin = buffer.PinAll();
        ulong pointer = buffer.Reserve(0, out _, out _);
        BloomFilter bloom = buffer.FindBloom(pointer)!;
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
        using NodeRecordBuffer nodeBuffer = new(0, length);
        nodeBuffer.Reserve(NodeRecordCodec.RecordAlignment, out NativeNodeBuffer buffer, out _);
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
        using NodeRecordBuffer buffer = new(0, GenerationBytes);
        using NodeRecordBufferPin first = buffer.Pin();
        ulong firstPointer = buffer.Reserve(NodeRecordCodec.RecordAlignment, out _, out _);
        using NodeRecordBufferPin second = buffer.Pin();
        ulong secondPointer = buffer.Reserve(NodeRecordCodec.RecordAlignment, out _, out _);
        using NodeRecordBufferPin copy = first.Copy();
        buffer.Reserve(GenerationBytes, out _, out _);
        buffer.Reserve(NodeRecordCodec.RecordAlignment, out _, out _);
        using NodeRecordBufferPin rotated = buffer.Pin();
        ulong rotatedPointer = buffer.Reserve(NodeRecordCodec.RecordAlignment, out _, out _);

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
        using NodeRecordBuffer buffer = new(0, GenerationBytes);
        object active = typeof(NodeRecordBuffer).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(buffer)!;
        active.GetType().GetField("Used")!.SetValue(active, used);
        using NodeRecordBufferPin retain = buffer.Pin();
        ulong pointer = buffer.Reserve(NodeRecordCodec.RecordAlignment, out _, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retain.WriteBoundary, Is.EqualTo(retain.Generation << NodeRecordCodec.OffsetBits | GenerationBytes));
            Assert.That(pointer, Is.GreaterThan(retain.WriteBoundary));
        }
    }

    [Test]
    public void Concurrent_reservations_never_precede_their_retain_boundary()
    {
        using NodeRecordBuffer buffer = new(0, GenerationBytes);
        Parallel.For(0, 16, thread =>
        {
            for (int i = 0; i < 1024; i++)
            {
                using NodeRecordBufferPin retain = buffer.Pin();
                ulong pointer = buffer.Reserve(NodeRecordCodec.RecordAlignment, out _, out _);
                Assert.That(pointer, Is.GreaterThanOrEqualTo(retain.WriteBoundary));
            }
        });
    }

    [Test]
    public void Native_buffer_disposal_is_idempotent()
    {
        using NativeNodeBuffer buffer = NativeNodeBuffer.Allocate(1);
        ((IDisposable)buffer).Dispose();
        ((IDisposable)buffer).Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.IsDisposed, Is.True);
            Assert.That(() => buffer.AsSpan().ToArray(), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void Pin_all_keeps_generations_in_place_until_every_pin_is_released([Values] bool compressed, [Values] bool overlapping)
    {
        const int generationBytes = 4096;
        using NodeRecordBuffer buffer = new(2048, generationBytes);
        buffer.Reserve(0, out NativeNodeBuffer original, out _);
        byte[] rlp = Leaf(1, 1000);
        ulong pointer = AppendAt(buffer, TestItem.KeccakA, rlp);
        if (compressed)
        {
            AdvanceTo(LiveGenerations + 1);
            Assert.That(() => original.IsDisposed, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        }

        IDisposable first = buffer.PinAll();
        IDisposable second = overlapping ? buffer.PinAll() : first;
        Assert.That(buffer.TryLocate(pointer, out NodeRecord record), Is.True);
        ValueRef pendingValue = record.LeafValue;
        byte[] expectedValue = pendingValue.Span.ToArray();
        ulong later = AppendAt(buffer, TestItem.KeccakB, Leaf(2, 1000));
        AdvanceTo(GenerationOf(later) + 2 * LiveGenerations);
        if (overlapping) first.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.Rlp.ToArray(), Is.EqualTo(rlp));
            Assert.That(pendingValue.Span.ToArray(), Is.EqualTo(expectedValue), "a pending bulk-set value borrows the same pinned storage");
            Assert.That(buffer.FindBloom(later), Is.Not.Null, "a pinned generation is not compacted");
            Assert.That(original.IsDisposed, Is.EqualTo(compressed));
        }

        second.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => buffer.FindBloom(later) is null, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "released generations are compacted");
            Assert.That(() => IsLocatable(buffer, pointer), Is.False.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "and evicted over the budget");
            Assert.That(() => record.Rlp.ToArray(), Throws.InstanceOf<ObjectDisposedException>(), "the released native owner is freed");
        }

        void AdvanceTo(ulong generation)
        {
            while (GenerationOf(buffer.Reserve(generationBytes, out _, out _)) < generation) { }
        }
    }

    [Test]
    public void Lookup_uses_full_generation_ids_when_the_ring_slot_is_reused()
    {
        const int generationBytes = 256;
        using NodeRecordBuffer buffer = new(0, generationBytes);
        byte[] previous = Leaf(1, 8);
        byte[] current = Leaf(2, 8);
        ulong pointer = Append(buffer, previous);
        AdvanceTo(LiveGenerations + 1);
        Assert.That(() => buffer.FindBloom(pointer) is null, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        AdvanceTo(4096);
        ulong replacement = Append(buffer, current);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GenerationOf(replacement), Is.EqualTo(GenerationOf(pointer) + 4096));
            Assert.That((uint)replacement, Is.EqualTo((uint)pointer));
            Assert.That(IsLocatable(buffer, pointer), Is.False, "the older full generation id of the same slot and offset misses");
            Assert.That(buffer.GetRecord(replacement).Rlp.ToArray(), Is.EqualTo(current));
            Assert.That(buffer.TryLocate(pointer + (8192UL << 32), out _), Is.False, "same slot and offset do not match another full generation id");
        }

        void AdvanceTo(ulong generation)
        {
            while (GenerationOf(buffer.Reserve(generationBytes, out _, out _)) < generation) { }
        }
    }

    [Test]
    public void Shutdown_defers_free_until_every_pin_is_released([Values] bool readerFinishesFirst)
    {
        using NodeRecordBuffer buffer = new(0, GenerationBytes);
        using NodeRecordBufferPin retain = buffer.Pin();
        ulong pointer = Append(buffer, Leaf(1, 8));
        NodeRecord record = buffer.GetRecord(pointer);
        BloomFilter bloom = buffer.FindBloom(pointer)!;
        ulong nodeKeyHash = NodeRecordCodec.NodeKeyHash(null, TreePath.Empty);
        buffer.Reserve(0, out NativeNodeBuffer storage, out _);
        IDisposable reader = buffer.PinAll();
        buffer.Dispose();
        buffer.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.IsDisposed, Is.False);
            Assert.That(bloom.MightContain(nodeKeyHash), Is.True);
            Assert.That(Matches(record, Leaf(1, 8)), Is.True);
            Assert.That(() => buffer.Pin(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => retain.Copy(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => buffer.GetRecord(pointer), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => buffer.Reserve(8, out _, out _), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => buffer.PinAll(), Throws.InstanceOf<ObjectDisposedException>());
        }

        if (readerFinishesFirst) reader.Dispose();
        else retain.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.IsDisposed, Is.False, "the other owner still borrows native memory");
            Assert.That(bloom.MightContain(nodeKeyHash), Is.True);
            Assert.That(Matches(record, Leaf(1, 8)), Is.True);
        }
        if (readerFinishesFirst) retain.Dispose();
        else reader.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.IsDisposed, Is.True);
            Assert.That(() => bloom.MightContain(nodeKeyHash), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void Shutdown_releases_storage_when_queued_compaction_finishes()
    {
        using NodeRecordBuffer buffer = new(0, GenerationBytes);
        ulong pointer = buffer.Reserve(GenerationBytes, out NativeNodeBuffer first, out _);
        BloomFilter bloom = buffer.FindBloom(pointer)!;
        while (GenerationOf(buffer.Reserve(GenerationBytes, out _, out _)) < LiveGenerations + 1) { }
        buffer.Dispose();
        Assert.That(() => first.IsDisposed, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        // Wait for the disposal lock to cover the rest of this generation's cleanup, too.
        buffer.Dispose();
        Assert.That(() => bloom.MightContain(0), Throws.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void Compaction_failure_is_reported_and_does_not_prevent_shutdown_cleanup()
    {
        using NodeRecordBuffer buffer = new(0, GenerationBytes);
        ulong pointer = buffer.Reserve(GenerationBytes, out NativeNodeBuffer malformed, out _);
        BloomFilter bloom = buffer.FindBloom(pointer)!;
        malformed.AsSpan()[0] = (byte)NodeRecordKind.Leaf;
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(NodeRecordCodec.RlpLengthOffset), GenerationBytes);
        while (GenerationOf(buffer.Reserve(GenerationBytes, out _, out _)) < LiveGenerations + 1) { }
        Assert.That(CompactionFailed, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        buffer.Dispose();
        Assert.That(() => malformed.IsDisposed, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        buffer.Dispose();
        Assert.That(() => bloom.MightContain(0), Throws.InstanceOf<ObjectDisposedException>());

        bool CompactionFailed()
        {
            try
            {
                using IDisposable pin = buffer.PinAll();
                return false;
            }
            catch (TrieException exception) when (exception.Message == "Trie node buffer compaction failed.")
            {
                return true;
            }
        }
    }

    [Test]
    public void Records_resolve_in_place_until_their_generation_drops()
    {
        using NodeRecordBuffer buffer = new(0, GenerationBytes);
        using NodeRecordBufferPin first = buffer.Pin();
        NodeRecordBufferPin middle = null;
        NodeRecordBufferPin middleCopy = null;
        List<(ulong Pointer, byte[] Rlp)> records = [];
        while (records.Count == 0 || GenerationOf(records[^1].Pointer) < 24)
        {
            if (middle is null && records.Count > 0 && GenerationOf(records[^1].Pointer) == 6)
            {
                middle = buffer.Pin();
                middleCopy = middle.Copy();
            }

            byte[] rlp = Leaf(records.Count, 1000);
            records.Add((Append(buffer, rlp), rlp));
        }

        ulong activeGeneration = GenerationOf(records[^1].Pointer);
        Assert.That(records.Select(record => Resolves(buffer, record)), Is.All.True, "everything resolves while the first generation is retained");

        middle.Dispose();
        middle.Dispose();
        first.Dispose();
        AssertResolvesFrom(buffer, records, middleCopy.Generation, "a copy keeps its generation and every later one after the original is disposed");
        Assert.That(() => buffer.GetRecord(records[0].Pointer), Throws.InstanceOf<TrieException>());

        middleCopy.Dispose();
        AssertResolvesFrom(buffer, records, activeGeneration - LiveGenerations + 1, "only the live generations resolve after the last release");

        byte[] oversize = Leaf(-1, GenerationBytes + 1);
        ulong oversizePointer = Append(buffer, oversize);
        ulong nextPointer = Append(buffer, Leaf(-2, 1000));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.TryLocate(0, out _), Is.False, "pointer 0");
            Assert.That(buffer.TryLocate((activeGeneration + 100) << 32, out _), Is.False, "unknown generation");
            Assert.That(Resolves(buffer, (oversizePointer, oversize)), Is.True, "oversize record");
            Assert.That(oversizePointer, Is.EqualTo((activeGeneration + 1) << 32), "oversize record starts its own generation");
            Assert.That(GenerationOf(nextPointer), Is.EqualTo(activeGeneration + 2), "the record after an oversize one");
        }
    }

    [Test]
    public void Retain_copy_cannot_resurrect_a_generation_dropped_by_a_concurrent_release()
    {
        using NodeRecordBuffer buffer = new(0, GenerationBytes);
        using NodeRecordBufferPin retain = buffer.Pin();
        while (GenerationOf(buffer.Reserve(GenerationBytes, out _, out _)) < LiveGenerations + 1) { }
        retain.Dispose();

        // Copy may have passed its disposed check before the releasing thread acquired the buffer lock.
        Assert.That(() => buffer.AddPin(retain.Generation), Throws.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void Root_index_returns_the_newest_matching_root([Values] bool storage)
    {
        using NodeRecordBuffer buffer = new(0, GenerationBytes);
        Hash256 address = storage ? TestItem.KeccakA : null;
        NodeRecordBufferPin retain = buffer.Pin();
        ulong droppedPointer = Append(buffer, Leaf(-1, 8));

        ValueHash256[] roots = new ValueHash256[1500];
        ulong[] pointers = new ulong[roots.Length];
        for (int i = 0; i < roots.Length; i++)
        {
            roots[i] = Root(i);
            pointers[i] = Append(buffer, Leaf(i, 8));
            buffer.SetRoot(address, roots[i], pointers[i]);
        }

        Hash256 otherAddress = storage ? SameSlotAddress(address) : Hash256.Zero;
        ulong otherAddressPointer = Append(buffer, Leaf(-2, 8));
        buffer.SetRoot(otherAddress, roots[0], otherAddressPointer);
        pointers[1] = Append(buffer, Leaf(-3, 8));
        buffer.SetRoot(address, roots[1], pointers[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roots.Select(root => buffer.FindRoot(address, root)), Is.EqualTo(pointers), "newest pointer of every root");
            Assert.That(buffer.FindRoot(otherAddress, roots[0]), Is.EqualTo(otherAddressPointer), "the same root under another address");
            Assert.That(buffer.FindRoot(address, Root(-1)), Is.Zero, "a root never set");
        }

        retain.Dispose();
        while (GenerationOf(Append(buffer, Leaf(0, 1000))) < GenerationOf(droppedPointer) + LiveGenerations) { }

        long missesBefore = Nethermind.State.Flat.Metrics.NodeRecordBufferRootMisses;
        buffer.SetRoot(address, Root(-2), droppedPointer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => buffer.FindRoot(address, Root(-2)), Is.Zero.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "a root whose record dropped");
            Assert.That(() => buffer.FindRoot(address, roots[0]), Is.Zero.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "a root whose root key dropped");
            Assert.That(Nethermind.State.Flat.Metrics.NodeRecordBufferRootMisses, Is.GreaterThanOrEqualTo(missesBefore + 2), "misses are counted");
        }
    }

    [Test]
    public void Dropped_generation_keeps_last_record_per_path_not_rewritten_later([Values(0UL, 1UL << 20)] ulong compressedBudget)
    {
        const ulong scenarioGeneration = 32;
        using NodeRecordBuffer buffer = new(compressedBudget, GenerationBytes);
        List<(ulong Pointer, byte[] Rlp)> unique = [];
        AppendUniqueUntil(scenarioGeneration);
        (ulong Pointer, byte[] Rlp) superseded = AppendRecord(TestItem.KeccakA, Leaf(-1, 8));
        (ulong Pointer, byte[] Rlp) kept = AppendRecord(TestItem.KeccakA, Leaf(-2, 8));
        (ulong Pointer, byte[] Rlp) rewritten = AppendRecord(TestItem.KeccakB, Leaf(-3, 8));
        buffer.SetRoot(null, Root(-1), kept.Pointer);
        AppendUniqueUntil(scenarioGeneration + 1);
        (ulong Pointer, byte[] Rlp) rewrite = AppendRecord(TestItem.KeccakB, Leaf(-4, 8));
        AppendUniqueUntil(scenarioGeneration + LiveGenerations);

        bool keeps = compressedBudget > 0;
        // The scenario generation is the newest dropped one and generations are compacted and evicted oldest first.
        Assert.That(() => (IsLocatable(buffer, superseded.Pointer), buffer.FindRoot(null, Root(-1))), Is.EqualTo((false, keeps ? kept.Pointer : 0UL)).After(CompactionTimeoutMilliseconds, PollingMilliseconds),
            "the scenario generation is compacted, keeping its root key only within the budget");
        ulong oldestResolving = unique.Where(record => Resolves(buffer, record)).Min(record => GenerationOf(record.Pointer));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new[] { superseded, kept, rewritten }.Select(record => GenerationOf(record.Pointer)), Is.All.EqualTo(scenarioGeneration), "scenario generation");
            Assert.That(Resolves(buffer, kept), Is.EqualTo(keeps), "the last record of a path not written later");
            Assert.That(Resolves(buffer, rewritten), Is.False, "a record of a path written later");
            Assert.That(Resolves(buffer, rewrite), Is.True, "a live record");
            Assert.That(unique.Select(record => Resolves(buffer, record)), Is.EqualTo(unique.Select(record => GenerationOf(record.Pointer) >= oldestResolving)), "compressed generations are evicted oldest first");
            Assert.That(oldestResolving, keeps ? Is.InRange(2UL, scenarioGeneration) : Is.EqualTo(scenarioGeneration + 1), "oldest generation still resolving");
        }

        (ulong Pointer, byte[] Rlp) AppendRecord(Hash256 address, byte[] rlp) => (AppendAt(buffer, address, rlp), rlp);

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
        using NodeRecordBuffer buffer = new(1 << 20, GenerationBytes);
        using NodeRecordBufferPin retain = compacting ? null : buffer.Pin();
        ulong[][] pointers = new ulong[threadCount][];
        NodeRecordBufferPin[] threadRetains = new NodeRecordBufferPin[threadCount];

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, thread =>
        {
            pointers[thread] = new ulong[recordsPerThread];
            for (int i = 0; i < recordsPerThread; i++)
            {
                // Like a scope per block, a writer retains a generation no newer than those it writes.
                if (i % recordsPerRetain == 0)
                {
                    threadRetains[thread]?.Dispose();
                    threadRetains[thread] = buffer.Pin();
                }

                int seed = thread * recordsPerThread + i;
                pointers[thread][i] = AppendAt(buffer, RecordAddress(seed), Leaf(seed, 8));
                buffer.SetRoot(ConcurrentAddress(thread), Root(seed), pointers[thread][i]);
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
                using IDisposable pin = buffer.PinAll();
                bool located = buffer.TryLocate(pointer, out NodeRecord record);
                if (located ? !Matches(record, Leaf(seed, 8)) : retained) failures.Add($"record {seed}");
                ulong root = buffer.FindRoot(ConcurrentAddress(thread), Root(seed));
                if (root != pointer && (retained || root != 0)) failures.Add($"root {seed}");
            }
        }

        foreach (NodeRecordBufferPin threadRetain in threadRetains) threadRetain.Dispose();
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

    private static ulong Append(NodeRecordBuffer buffer, byte[] rlp) => AppendAt(buffer, null, rlp);

    private static ulong AppendAt(NodeRecordBuffer buffer, Hash256 address, byte[] rlp) => buffer.AppendRecordFromRlp(address, TreePath.Empty, ValueKeccak.Compute(rlp), rlp);

    internal static byte[] Leaf(int seed, int valueLength)
    {
        byte[] value = new byte[valueLength];
        BinaryPrimitives.WriteInt32LittleEndian(value, seed);
        byte[] keyNibbles = [1, 2];
        byte[] rlp = new byte[NodeRlp.LeafLength(keyNibbles, value)];
        NodeRlp.WriteLeaf(keyNibbles, value, rlp);
        return rlp;
    }

    internal static bool IsLocatable(NodeRecordBuffer buffer, ulong pointer)
    {
        using IDisposable pin = buffer.PinAll();
        return buffer.TryLocate(pointer, out _);
    }

    private static bool Resolves(NodeRecordBuffer buffer, (ulong Pointer, byte[] Rlp) record)
    {
        using IDisposable pin = buffer.PinAll();
        return buffer.TryLocate(record.Pointer, out NodeRecord located) && Matches(located, record.Rlp);
    }

    private static bool Matches(NodeRecord record, byte[] rlp) => record.Rlp.SequenceEqual(rlp) && record.Hash == ValueKeccak.Compute(rlp);

    /// <remarks>Dropped generations stay readable in place until they are compacted in the background.</remarks>
    private static void AssertResolvesFrom(NodeRecordBuffer buffer, List<(ulong Pointer, byte[] Rlp)> records, ulong oldestReadable, string message) =>
        Assert.That(() => records.Select(record => Resolves(buffer, record)).ToArray(), Is.EqualTo(records.Select(record => GenerationOf(record.Pointer) >= oldestReadable)).After(CompactionTimeoutMilliseconds, PollingMilliseconds), message);
}
