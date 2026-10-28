// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test.NodeBuffer;

public class PbtGroupRecordBufferTests
{
    private const int GenerationBytes = 64 * 1024;
    private const int RecordAlignment = PbtGroupRecordCodec.RecordAlignment;
    public const int LiveGenerations = PbtGroupRecordBuffer.LiveGenerations;
    public const int CompactionTimeoutMilliseconds = 10_000;
    public const int PollingMilliseconds = 10;

    [Test]
    public void Contract_write_batch_holds_every_record_written_to_it()
    {
        using PbtGroupRecordBuffer buffer = new(0, GenerationBytes);
        using IDisposable pin = buffer.PinAll();
        Append(buffer, Payload(0, 8));
        byte[][] payloads = [Payload(1, 8), Payload(2, 8)];
        using PbtGroupRecordBuffer scratch = new(0, GenerationBytes);
        byte[][] records = [.. payloads.Select(payload => scratch.TryLocate(Append(scratch, payload), out PbtGroupRecord record) ? record.Span.ToArray() : [])];
        int recordLength = PbtGroupRecordCodec.AlignedLength(records[0].Length);
        ulong pointer;
        int length;
        using (IPbtGroupRecordWriteBatch batch = ((IPbtGroupRecordBuffer)buffer).StartWriteBatch(recordLength * 2))
        {
            pointer = batch.BasePointer;
            length = batch.Bytes.Length;
            for (int i = 0; i < records.Length; i++) records[i].CopyTo(batch.Bytes[(i * recordLength)..]);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(length, Is.EqualTo(recordLength * 2));
            for (int i = 0; i < payloads.Length; i++)
                Assert.That(buffer.TryLocate(pointer + (ulong)(i * recordLength), out PbtGroupRecord record) && record.Payload.Span.SequenceEqual(payloads[i]), Is.True);
        }
    }

    [Test]
    public void Generation_ids_stop_at_the_pointer_generation_width()
    {
        // The top generation bit tags the warmed groups' transient pointers.
        ulong last = PbtGroupRecordBuffer.NextGeneration(PbtGroupRecordCodec.TransientGenerationMask - 2UL);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(last, Is.EqualTo(PbtGroupRecordCodec.TransientGenerationMask - 1UL));
            Assert.That(() => PbtGroupRecordBuffer.NextGeneration(last), Throws.InvalidOperationException);
            Assert.That(() => PbtGroupRecordBuffer.NextGeneration(ulong.MaxValue), Throws.InvalidOperationException, "cannot wrap");
        }
    }

    [Test]
    public void Native_buffers_are_zero_filled([Values(0, 1, GenerationBytes, 2 * 1024 * 1024 - 1, 2 * 1024 * 1024, 2 * 1024 * 1024 + 1)] int length, [Values] bool prefault)
    {
        using PbtNativeGroupBuffer buffer = PbtNativeGroupBuffer.Allocate(length);
        if (prefault) buffer.Prefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.Length, Is.EqualTo(length));
            Assert.That(buffer.GetSpan().IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
        }
    }

    [Test]
    public void Generations_smaller_than_a_bloom_key_still_take_and_compact_records([Values(0, 1, 127, 128, GenerationBytes)] int generationBytes)
    {
        using PbtGroupRecordBuffer buffer = new(1UL << 20, generationBytes);
        byte[] payload = Payload(1, 8);
        ulong pointer = AppendAt(buffer, PathOf(1), payload);
        Assert.That(buffer.TryLocate(pointer, out PbtGroupRecord record) && record.Payload.Span.SequenceEqual(payload), Is.True);
        while (GenerationOf(Append(buffer, Payload(0, 8))) < GenerationOf(pointer) + LiveGenerations) { }
        Assert.That(() => IsFreed(record), Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "the dropped generation is compacted");
        Assert.That(Resolves(buffer, (pointer, payload)), Is.True, "and keeps its record");
    }

    [Test]
    public void Generation_buffers_stay_at_the_same_native_address_during_compacting_collections([Values(2 * 1024 * 1024, 4 * 1024 * 1024 + 1)] int length)
    {
        using PbtNativeGroupBuffer buffer = PbtNativeGroupBuffer.Allocate(length);
        ref byte address = ref MemoryMarshal.GetReference(buffer.GetSpan());
        buffer.GetSpan()[0] = 1;
        buffer.GetSpan()[^1] = 2;

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
            Assert.That(Unsafe.AreSame(ref address, ref MemoryMarshal.GetReference(buffer.GetSpan())), Is.True);
            Assert.That(buffer.GetSpan()[0], Is.EqualTo(1));
            Assert.That(buffer.GetSpan()[^1], Is.EqualTo(2));
        }
    }

    [Test]
    public void Retains_capture_exact_allocation_boundaries_and_copies_preserve_them()
    {
        using PbtGroupRecordBuffer buffer = new(0, GenerationBytes);
        using PbtGroupRecordBufferPin first = buffer.PinHead();
        ulong firstPointer = Reserve(buffer, RecordAlignment);
        using PbtGroupRecordBufferPin second = buffer.PinHead();
        ulong secondPointer = Reserve(buffer, RecordAlignment);
        using PbtGroupRecordBufferPin copy = first.Copy();
        Reserve(buffer, GenerationBytes);
        Reserve(buffer, RecordAlignment);
        using PbtGroupRecordBufferPin rotated = buffer.PinHead();
        ulong rotatedPointer = Reserve(buffer, RecordAlignment);

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
        using PbtGroupRecordBuffer buffer = new(0, GenerationBytes);
        object active = typeof(PbtGroupRecordBuffer).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(buffer)!;
        active.GetType().GetField("Used")!.SetValue(active, used);
        using PbtGroupRecordBufferPin retain = buffer.PinHead();
        ulong pointer = Reserve(buffer, RecordAlignment);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retain.WriteBoundary, Is.EqualTo(retain.Generation << PbtGroupRecordBuffer.OffsetBits | GenerationBytes));
            Assert.That(pointer, Is.GreaterThan(retain.WriteBoundary));
        }
    }

    [Test]
    public void Concurrent_reservations_never_precede_their_retain_boundary()
    {
        using PbtGroupRecordBuffer buffer = new(0, GenerationBytes);
        Parallel.For(0, 16, thread =>
        {
            for (int i = 0; i < 1024; i++)
            {
                using PbtGroupRecordBufferPin retain = buffer.PinHead();
                ulong pointer = Reserve(buffer, RecordAlignment);
                Assert.That(pointer, Is.GreaterThanOrEqualTo(retain.WriteBoundary));
            }
        });
    }

    [Test]
    public void Native_buffer_disposal_is_idempotent()
    {
        using PbtNativeGroupBuffer buffer = PbtNativeGroupBuffer.Allocate(1);
        ((IDisposable)buffer).Dispose();
        ((IDisposable)buffer).Dispose();
        Assert.That(() => buffer.GetSpan().ToArray(), Throws.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void Pin_all_keeps_generations_in_place_until_every_pin_is_released([Values] bool compressed, [Values] bool overlapping)
    {
        const int generationBytes = 4096;
        using PbtGroupRecordBuffer buffer = new(2048, generationBytes);
        byte[] payload = Payload(1, 1000);
        ulong pointer = AppendAt(buffer, PathOf(-1), payload);
        Assert.That(buffer.TryLocate(pointer, out PbtGroupRecord original), Is.True);
        if (compressed)
        {
            AdvanceTo(LiveGenerations + 1);
            Assert.That(() => IsFreed(original), Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        }

        IDisposable first = buffer.PinAll();
        IDisposable second = overlapping ? buffer.PinAll() : first;
        Assert.That(buffer.TryLocate(pointer, out PbtGroupRecord record), Is.True);
        ulong later = AppendAt(buffer, PathOf(-2), Payload(2, 1000));
        Assert.That(buffer.TryLocate(later, out PbtGroupRecord laterRecord), Is.True);
        AdvanceTo(GenerationOf(later) + 2 * LiveGenerations);
        if (overlapping) first.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.Payload.ToArray(), Is.EqualTo(payload));
            Assert.That(IsFreed(laterRecord), Is.False, "a pinned generation is not compacted");
            Assert.That(IsFreed(original), Is.EqualTo(compressed));
        }

        second.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => IsFreed(laterRecord), Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "released generations are compacted");
            Assert.That(() => IsLocatable(buffer, pointer), Is.False.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "and evicted over the budget");
            Assert.That(() => IsFreed(record), Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "the released native owner is freed");
        }

        void AdvanceTo(ulong generation)
        {
            while (GenerationOf(Reserve(buffer, generationBytes)) < generation) { }
        }
    }

    [Test]
    public void Overlapping_pin_alls_do_not_keep_old_generations_from_being_freed([Values] bool compressed)
    {
        using PbtGroupRecordBuffer buffer = new(compressed ? 1UL << 20 : 0, GenerationBytes);
        ulong pointer = Append(buffer, Payload(1, 8));
        Assert.That(buffer.TryLocate(pointer, out PbtGroupRecord original), Is.True);
        IDisposable reader = buffer.PinAll();
        Assert.That(buffer.TryLocate(pointer, out PbtGroupRecord record), Is.True);
        // Each reader is pinned before the previous one is released, so some reader always pins the old generations.
        for (int i = 0; i < 2 * LiveGenerations; i++)
        {
            Reserve(buffer, GenerationBytes);
            IDisposable next = buffer.PinAll();
            if (i == 0) Assert.That(record.Payload.Span.SequenceEqual(Payload(1, 8)), Is.True, "a borrowed record stays valid while its reader pins it");
            reader.Dispose();
            reader = next;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => IsFreed(original), Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "the in-place storage is freed");
            Assert.That(() => IsLocatable(buffer, pointer), Is.EqualTo(compressed).After(CompactionTimeoutMilliseconds, PollingMilliseconds), "the compacted record resolves within the budget");
        }

        reader.Dispose();
    }

    [Test]
    public void Lookup_uses_full_generation_ids_when_the_ring_slot_is_reused()
    {
        const int generationBytes = 256;
        using PbtGroupRecordBuffer buffer = new(0, generationBytes);
        byte[] previous = Payload(1, 8);
        byte[] current = Payload(2, 8);
        ulong pointer = Append(buffer, previous);
        Assert.That(buffer.TryLocate(pointer, out PbtGroupRecord original), Is.True);
        AdvanceTo(LiveGenerations + 1);
        Assert.That(() => IsFreed(original), Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        AdvanceTo(4096);
        ulong replacement = Append(buffer, current);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GenerationOf(replacement), Is.EqualTo(GenerationOf(pointer) + 4096));
            Assert.That((uint)replacement, Is.EqualTo((uint)pointer));
            Assert.That(IsLocatable(buffer, pointer), Is.False, "the older full generation id of the same slot and offset misses");
            Assert.That(buffer.TryLocate(replacement, out PbtGroupRecord record) && record.Payload.Span.SequenceEqual(current), Is.True);
            Assert.That(buffer.TryLocate(pointer + (8192UL << 32), out _), Is.False, "same slot and offset do not match another full generation id");
        }

        void AdvanceTo(ulong generation)
        {
            while (GenerationOf(Reserve(buffer, generationBytes)) < generation) { }
        }
    }

    [Test]
    public void Shutdown_defers_free_until_every_pin_is_released([Values] bool readerFinishesFirst)
    {
        using PbtGroupRecordBuffer buffer = new(0, GenerationBytes);
        using PbtGroupRecordBufferPin retain = buffer.PinHead();
        ulong pointer = Append(buffer, Payload(1, 8));
        Assert.That(buffer.TryLocate(pointer, out PbtGroupRecord record), Is.True);
        IDisposable reader = buffer.PinAll();
        buffer.Dispose();
        buffer.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.Payload.Span.SequenceEqual(Payload(1, 8)), Is.True);
            Assert.That(() => buffer.PinHead(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => retain.Copy(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => Reserve(buffer, 8), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => buffer.PinAll(), Throws.InstanceOf<ObjectDisposedException>());
        }

        if (readerFinishesFirst) reader.Dispose();
        else retain.Dispose();
        Assert.That(record.Payload.Span.SequenceEqual(Payload(1, 8)), Is.True, "the other owner still borrows native memory");
        if (readerFinishesFirst) retain.Dispose();
        else reader.Dispose();
        Assert.That(IsFreed(record), Is.True);
    }

    [Test]
    public void Shutdown_releases_storage_when_queued_compaction_finishes()
    {
        using PbtGroupRecordBuffer buffer = new(0, GenerationBytes);
        Assert.That(buffer.TryLocate(Reserve(buffer, GenerationBytes), out PbtGroupRecord first), Is.True);
        while (GenerationOf(Reserve(buffer, GenerationBytes)) < LiveGenerations + 1) { }
        buffer.Dispose();
        Assert.That(() => IsFreed(first), Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
    }

    [Test]
    public void Compaction_failure_is_reported_and_does_not_prevent_shutdown_cleanup()
    {
        using PbtGroupRecordBuffer buffer = new(0, GenerationBytes);
        ulong pointer;
        using (IPbtGroupRecordWriteBatch batch = buffer.StartWriteBatch(GenerationBytes))
        {
            pointer = batch.BasePointer;
            // A garbage header claims a far larger payload than the generation holds.
            MemoryMarshal.AsRef<PbtGroupRecordHeader>(batch.Bytes).PayloadLength = 2 * GenerationBytes;
        }

        Assert.That(buffer.TryLocate(pointer, out PbtGroupRecord malformed), Is.True);
        while (GenerationOf(Reserve(buffer, GenerationBytes)) < LiveGenerations + 1) { }
        Assert.That(CompactionFailed, Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));
        buffer.Dispose();
        Assert.That(() => IsFreed(malformed), Is.True.After(CompactionTimeoutMilliseconds, PollingMilliseconds));

        bool CompactionFailed()
        {
            try
            {
                using IDisposable pin = buffer.PinAll();
                return false;
            }
            catch (InvalidOperationException exception) when (exception.Message == "PBT group record buffer compaction failed.")
            {
                return true;
            }
        }
    }

    [Test]
    public void Records_resolve_in_place_until_their_generation_drops()
    {
        using PbtGroupRecordBuffer buffer = new(0, GenerationBytes);
        using PbtGroupRecordBufferPin first = buffer.PinHead();
        PbtGroupRecordBufferPin? middle = null;
        PbtGroupRecordBufferPin? middleCopy = null;
        List<(ulong Pointer, byte[] Payload)> records = [];
        while (records.Count == 0 || GenerationOf(records[^1].Pointer) < 24)
        {
            if (middle is null && records.Count > 0 && GenerationOf(records[^1].Pointer) == 6)
            {
                middle = buffer.PinHead();
                middleCopy = middle.Copy();
            }

            byte[] payload = Payload(records.Count, 1000);
            records.Add((Append(buffer, payload), payload));
        }

        ulong activeGeneration = GenerationOf(records[^1].Pointer);
        Assert.That(records.Select(record => Resolves(buffer, record)), Is.All.True, "everything resolves while the first generation is retained");

        middle!.Dispose();
        middle.Dispose();
        first.Dispose();
        AssertResolvesFrom(buffer, records, middleCopy!.Generation, "a copy keeps its generation and every later one after the original is disposed");

        middleCopy.Dispose();
        AssertResolvesFrom(buffer, records, activeGeneration - LiveGenerations + 1, "only the live generations resolve after the last release");

        using PbtGroupRecordBufferPin oversizeRetain = buffer.PinHead();
        byte[] oversize = Payload(-1, GenerationBytes + 1);
        ulong oversizePointer = Append(buffer, oversize);
        ulong nextPointer = Append(buffer, Payload(-2, 1000));
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
        using PbtGroupRecordBuffer buffer = new(0, GenerationBytes);
        using PbtGroupRecordBufferPin retain = buffer.PinHead();
        while (GenerationOf(Reserve(buffer, GenerationBytes)) < LiveGenerations + 1) { }
        retain.Dispose();

        // Copy may have passed its disposed check before the releasing thread acquired the buffer lock.
        Assert.That(() => buffer.AddPin(retain.Generation), Throws.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void Root_index_returns_the_newest_matching_root()
    {
        using PbtGroupRecordBuffer buffer = new(0, GenerationBytes);
        PbtGroupRecordBufferPin retain = buffer.PinHead();
        ulong droppedPointer = Append(buffer, Payload(-1, 8));

        ValueHash256[] roots = new ValueHash256[1500];
        ulong[] pointers = new ulong[roots.Length];
        for (int i = 0; i < roots.Length; i++)
        {
            roots[i] = Root(i);
            pointers[i] = Append(buffer, Payload(i, 8));
            buffer.SetRoot(roots[i], pointers[i]);
        }

        pointers[1] = Append(buffer, Payload(-3, 8));
        buffer.SetRoot(roots[1], pointers[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roots.Select(root => buffer.FindRoot(root)), Is.EqualTo(pointers), "newest pointer of every root");
            Assert.That(buffer.FindRoot(Root(-1)), Is.Zero, "a root never set");
        }

        retain.Dispose();
        while (GenerationOf(Append(buffer, Payload(0, 1000))) < GenerationOf(droppedPointer) + LiveGenerations) { }

        buffer.SetRoot(Root(-2), droppedPointer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => buffer.FindRoot(Root(-2)), Is.Zero.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "a root whose record dropped");
            Assert.That(() => buffer.FindRoot(roots[0]), Is.Zero.After(CompactionTimeoutMilliseconds, PollingMilliseconds), "a root whose root key dropped");
        }
    }

    [Test]
    public void Dropped_generation_keeps_last_record_per_path_not_rewritten_later([Values(0UL, 1UL << 20)] ulong compressedBudget)
    {
        const ulong scenarioGeneration = 32;
        using PbtGroupRecordBuffer buffer = new(compressedBudget, GenerationBytes);
        List<(ulong Pointer, byte[] Payload)> unique = [];
        AppendUniqueUntil(scenarioGeneration);
        (ulong Pointer, byte[] Payload) superseded = AppendRecord(PathOf(-1), Payload(-1, 8));
        (ulong Pointer, byte[] Payload) kept = AppendRecord(PathOf(-1), Payload(-2, 8));
        (ulong Pointer, byte[] Payload) rewritten = AppendRecord(PathOf(-2), Payload(-3, 8));
        buffer.SetRoot(Root(-1), kept.Pointer);
        AppendUniqueUntil(scenarioGeneration + 1);
        (ulong Pointer, byte[] Payload) rewrite = AppendRecord(PathOf(-2), Payload(-4, 8));
        buffer.AddToBlooms(rewrite.Pointer, rewrite.Pointer);
        AppendUniqueUntil(scenarioGeneration + LiveGenerations);

        bool keeps = compressedBudget > 0;
        // The scenario generation is the newest dropped one and generations are compacted and evicted oldest first.
        Assert.That(() => (IsLocatable(buffer, superseded.Pointer), buffer.FindRoot(Root(-1))), Is.EqualTo((false, keeps ? kept.Pointer : 0UL)).After(CompactionTimeoutMilliseconds, PollingMilliseconds),
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

        (ulong Pointer, byte[] Payload) AppendRecord(PbtStorageNodePath path, byte[] payload) => (AppendAt(buffer, path, payload), payload);

        void AppendUniqueUntil(ulong generation)
        {
            while (unique.Count == 0 || GenerationOf(unique[^1].Pointer) < generation)
            {
                unique.Add(AppendRecord(PathOf(unique.Count), Payload(unique.Count, 1000)));
            }
        }
    }

    [Test]
    public void Concurrent_reservations_and_root_sets_are_disjoint_and_readable([Values] bool compacting)
    {
        const int threadCount = 16;
        const int recordsPerThread = 10_000;
        const int recordsPerRetain = 1_000;
        using PbtGroupRecordBuffer buffer = new(1 << 20, GenerationBytes);
        using PbtGroupRecordBufferPin? retain = compacting ? null : buffer.PinHead();
        ulong[][] pointers = new ulong[threadCount][];
        PbtGroupRecordBufferPin?[] threadRetains = new PbtGroupRecordBufferPin?[threadCount];

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, thread =>
        {
            pointers[thread] = new ulong[recordsPerThread];
            for (int i = 0; i < recordsPerThread; i++)
            {
                // Like a scope per block, a writer retains a generation no newer than those it writes.
                if (i % recordsPerRetain == 0)
                {
                    threadRetains[thread]?.Dispose();
                    threadRetains[thread] = buffer.PinHead();
                }

                int seed = thread * recordsPerThread + i;
                pointers[thread][i] = AppendAt(buffer, PathOf(seed), Payload(seed, 8));
                buffer.SetRoot(Root(seed), pointers[thread][i]);
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
                bool located = buffer.TryLocate(pointer, out PbtGroupRecord record);
                if (located ? !record.Payload.Span.SequenceEqual(Payload(seed, 8)) : retained) failures.Add($"record {seed}");
                ulong root = buffer.FindRoot(Root(seed));
                if (root != pointer && (retained || root != 0)) failures.Add($"root {seed}");
            }
        }

        foreach (PbtGroupRecordBufferPin? threadRetain in threadRetains) threadRetain!.Dispose();
        Assert.That(failures, Is.Empty);
    }

    public static ulong GenerationOf(ulong pointer) => pointer >> 32;

    private static ValueHash256 Root(int seed) => ValueKeccak.Compute(BitConverter.GetBytes(seed));

    private static PbtStorageNodePath PathOf(int seed) => new(BitConverter.GetBytes(seed), 32);

    private static ulong Reserve(PbtGroupRecordBuffer buffer, int alignedTotalLength)
    {
        using IPbtGroupRecordWriteBatch batch = buffer.StartWriteBatch(alignedTotalLength);
        return batch.BasePointer;
    }

    private static ulong Append(IPbtGroupRecordBuffer buffer, byte[] payload) => AppendAt(buffer, default, payload);

    private static ulong AppendAt(IPbtGroupRecordBuffer buffer, PbtStorageNodePath path, byte[] payload) => buffer.Append(path, payload, default);

    public static byte[] Payload(int seed, int length)
    {
        byte[] payload = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, seed);
        return payload;
    }

    /// <summary>Whether the native storage <paramref name="record"/> borrows was released.</summary>
    public static bool IsFreed(PbtGroupRecord record)
    {
        try
        {
            _ = record.Kind;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    public static bool IsLocatable(PbtGroupRecordBuffer buffer, ulong pointer)
    {
        using IDisposable pin = buffer.PinAll();
        return buffer.TryLocate(pointer, out _);
    }

    private static bool Resolves(PbtGroupRecordBuffer buffer, (ulong Pointer, byte[] Payload) record)
    {
        using IDisposable pin = buffer.PinAll();
        return buffer.TryLocate(record.Pointer, out PbtGroupRecord located) && located.Payload.Span.SequenceEqual(record.Payload);
    }

    /// <remarks>Dropped generations are compacted and evicted in the background.</remarks>
    private static void AssertResolvesFrom(PbtGroupRecordBuffer buffer, List<(ulong Pointer, byte[] Payload)> records, ulong oldestReadable, string message) =>
        Assert.That(() => records.Select(record => Resolves(buffer, record)).ToArray(), Is.EqualTo(records.Select(record => GenerationOf(record.Pointer) >= oldestReadable)).After(CompactionTimeoutMilliseconds, PollingMilliseconds), message);
}
