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
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.NodeBuffer;

public class NodeRecordBufferTests
{
    private const int GenerationBytes = 64 * 1024;
    private const int RecordAlignment = 8;
    public const int LiveGenerations = NodeRecordBuffer.LiveGenerations;
    private const int FreeTimeoutMilliseconds = 10_000;
    private const int PollingMilliseconds = 10;

    [Test]
    public void Contract_write_batch_holds_every_record_written_to_it()
    {
        using NodeRecordBuffer buffer = new(GenerationBytes);
        using IDisposable pin = buffer.PinAll();
        buffer.AppendRecordFromRlp(null, TreePath.Empty, ValueKeccak.Compute(Leaf(0, 8)), Leaf(0, 8), []);
        byte[][] rlps = [Leaf(1, 8), Leaf(2, 8)];
        using NodeRecordBuffer scratch = new(GenerationBytes);
        byte[][] records = [.. rlps.Select(rlp => scratch.TryLocate(scratch.AppendRecordFromRlp(null, TreePath.Empty, ValueKeccak.Compute(rlp), rlp, []), out NodeRecord record) ? record.Span.ToArray() : [])];
        int recordLength = (records[0].Length + RecordAlignment - 1) & -RecordAlignment;
        ulong pointer;
        int length;
        using (INodeRecordWriteBatch batch = ((INodeRecordBuffer)buffer).StartWriteBatch(recordLength * 2))
        {
            pointer = batch.BasePointer;
            length = batch.Bytes.Length;
            for (int i = 0; i < records.Length; i++) records[i].CopyTo(batch.Bytes[(i * recordLength)..]);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(length, Is.EqualTo(recordLength * 2));
            for (int i = 0; i < rlps.Length; i++)
                Assert.That(buffer.TryLocate(pointer + (ulong)(i * recordLength), out NodeRecord record) && record.Rlp.SequenceEqual(rlps[i]), Is.True);
        }
    }

    [Test]
    public void Generation_ids_stop_before_the_transient_namespace()
    {
        ulong last = NodeRecordBuffer.NextGeneration((1UL << 31) - 2);
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
        using NodeRecordBuffer buffer = new(GenerationBytes);
        byte[] rlp = Leaf(1, 8);
        ulong realPointer = Append(buffer, rlp);
        using WarmedNodeBuffer warmed = new(1);
        ulong temporaryPointer = FlatTestHelpers.Warm(warmed, null, TreePath.Empty, rlp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.TryLocate(temporaryPointer, out _), Is.False);
            Assert.That(buffer.TryLocate(realPointer, out _), Is.True);
            Assert.That(buffer.EnumerateNodes(temporaryPointer, 0), Is.Empty);
            Assert.That(warmed.TryLocate(realPointer, out _), Is.False);
        }
    }

    [Test]
    public void Native_buffers_are_zero_filled([Values(0, 1, GenerationBytes, 2 * 1024 * 1024 - 1, 2 * 1024 * 1024, 2 * 1024 * 1024 + 1)] int length, [Values] bool prefault)
    {
        using NativeNodeBuffer buffer = NativeNodeBuffer.Allocate(length);
        if (prefault) buffer.Prefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.Length, Is.EqualTo(length));
            Assert.That(buffer.GetSpan().IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
        }
    }

    [Test]
    public void Generation_buffers_stay_at_the_same_native_address_during_compacting_collections([Values(2 * 1024 * 1024, 4 * 1024 * 1024 + 1)] int length)
    {
        using NodeRecordBuffer nodeBuffer = new(length);
        nodeBuffer.Reserve(RecordAlignment, out NativeNodeBuffer buffer, out _);
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
        using NodeRecordBuffer buffer = new(GenerationBytes);
        using NodeRecordBufferPin first = buffer.PinHead();
        ulong firstPointer = buffer.Reserve(RecordAlignment, out _, out _);
        using NodeRecordBufferPin second = buffer.PinHead();
        ulong secondPointer = buffer.Reserve(RecordAlignment, out _, out _);
        using NodeRecordBufferPin copy = first.Copy();
        buffer.Reserve(GenerationBytes, out _, out _);
        buffer.Reserve(RecordAlignment, out _, out _);
        using NodeRecordBufferPin rotated = buffer.PinHead();
        ulong rotatedPointer = buffer.Reserve(RecordAlignment, out _, out _);

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
        using NodeRecordBuffer buffer = new(GenerationBytes);
        object active = typeof(NodeRecordBuffer).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(buffer)!;
        active.GetType().GetField("Used")!.SetValue(active, used);
        using NodeRecordBufferPin retain = buffer.PinHead();
        ulong pointer = buffer.Reserve(RecordAlignment, out _, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retain.WriteBoundary, Is.EqualTo(retain.Generation << NodeRecordBuffer.OffsetBits | GenerationBytes));
            Assert.That(pointer, Is.GreaterThan(retain.WriteBoundary));
        }
    }

    [Test]
    public void Concurrent_reservations_never_precede_their_retain_boundary()
    {
        using NodeRecordBuffer buffer = new(GenerationBytes);
        Parallel.For(0, 16, thread =>
        {
            for (int i = 0; i < 1024; i++)
            {
                using NodeRecordBufferPin retain = buffer.PinHead();
                ulong pointer = buffer.Reserve(RecordAlignment, out _, out _);
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
            Assert.That(IsDisposed(buffer), Is.True);
            Assert.That(() => buffer.GetSpan().ToArray(), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void Pin_all_keeps_generations_in_place_until_every_pin_is_released([Values] bool overlapping)
    {
        const int generationBytes = 4096;
        using NodeRecordBuffer buffer = new(generationBytes);
        buffer.Reserve(0, out NativeNodeBuffer original, out _);
        byte[] rlp = Leaf(1, 1000);
        ulong pointer = AppendAt(buffer, TestItem.KeccakA, rlp);
        IDisposable first = buffer.PinAll();
        IDisposable second = overlapping ? buffer.PinAll() : first;
        Assert.That(buffer.TryLocate(pointer, out NodeRecord record), Is.True);
        AdvanceTo(GenerationOf(pointer) + 2 * LiveGenerations);
        if (overlapping) first.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.Rlp.ToArray(), Is.EqualTo(rlp));
            Assert.That(IsDisposed(original), Is.False, "a pinned generation is not freed");
        }

        second.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsLocatable(buffer, pointer), Is.False, "released generations are dropped");
            Assert.That(() => ThrowsDisposed(() => record.Rlp.ToArray()), Is.True.After(FreeTimeoutMilliseconds, PollingMilliseconds), "the released native owner is freed");
        }

        void AdvanceTo(ulong generation)
        {
            while (GenerationOf(buffer.Reserve(generationBytes, out _, out _)) < generation) { }
        }
    }

    [Test]
    public void Overlapping_pin_alls_do_not_keep_old_generations_from_being_freed()
    {
        using NodeRecordBuffer buffer = new(GenerationBytes);
        ulong pointer = Append(buffer, Leaf(1, 8));
        buffer.Reserve(0, out NativeNodeBuffer original, out _);
        IDisposable reader = buffer.PinAll();
        Assert.That(buffer.TryLocate(pointer, out NodeRecord record), Is.True);
        // Each reader is pinned before the previous one is released, so some reader always pins the old generations.
        for (int i = 0; i < 2 * LiveGenerations; i++)
        {
            buffer.Reserve(GenerationBytes, out _, out _);
            IDisposable next = buffer.PinAll();
            if (i == 0) Assert.That(Matches(record, Leaf(1, 8)), Is.True, "a borrowed record stays valid while its reader pins it");
            reader.Dispose();
            reader = next;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => IsDisposed(original), Is.True.After(FreeTimeoutMilliseconds, PollingMilliseconds), "the in-place storage is freed");
            Assert.That(IsLocatable(buffer, pointer), Is.False, "the dropped record misses");
        }

        reader.Dispose();
    }

    [Test]
    public void Lookup_uses_full_generation_ids_when_the_ring_slot_is_reused()
    {
        const int generationBytes = 256;
        using NodeRecordBuffer buffer = new(generationBytes);
        byte[] previous = Leaf(1, 8);
        byte[] current = Leaf(2, 8);
        ulong pointer = Append(buffer, previous);
        AdvanceTo(4096);
        ulong replacement = Append(buffer, current);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GenerationOf(replacement), Is.EqualTo(GenerationOf(pointer) + 4096));
            Assert.That((uint)replacement, Is.EqualTo((uint)pointer));
            Assert.That(IsLocatable(buffer, pointer), Is.False, "the older full generation id of the same slot and offset misses");
            Assert.That(buffer.TryLocate(replacement, out NodeRecord record) && record.Rlp.SequenceEqual(current), Is.True);
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
        using NodeRecordBuffer buffer = new(GenerationBytes);
        using NodeRecordBufferPin retain = buffer.PinHead();
        ulong pointer = Append(buffer, Leaf(1, 8));
        Assert.That(buffer.TryLocate(pointer, out NodeRecord record), Is.True);
        buffer.Reserve(0, out NativeNodeBuffer storage, out _);
        IDisposable reader = buffer.PinAll();
        buffer.Dispose();
        buffer.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsDisposed(storage), Is.False);
            Assert.That(Matches(record, Leaf(1, 8)), Is.True);
            Assert.That(() => buffer.PinHead(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => retain.Copy(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => buffer.Reserve(8, out _, out _), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => buffer.PinAll(), Throws.InstanceOf<ObjectDisposedException>());
        }

        if (readerFinishesFirst) reader.Dispose();
        else retain.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsDisposed(storage), Is.False, "the other owner still borrows native memory");
            Assert.That(Matches(record, Leaf(1, 8)), Is.True);
        }
        if (readerFinishesFirst) retain.Dispose();
        else reader.Dispose();
        Assert.That(IsDisposed(storage), Is.True);
    }

    [Test]
    public void Records_resolve_in_place_until_their_generation_drops()
    {
        using NodeRecordBuffer buffer = new(GenerationBytes);
        using NodeRecordBufferPin first = buffer.PinHead();
        NodeRecordBufferPin? middle = null;
        NodeRecordBufferPin? middleCopy = null;
        List<(ulong Pointer, byte[] Rlp)> records = [];
        while (records.Count == 0 || GenerationOf(records[^1].Pointer) < 24)
        {
            if (middle is null && records.Count > 0 && GenerationOf(records[^1].Pointer) == 6)
            {
                middle = buffer.PinHead();
                middleCopy = middle.Copy();
            }

            byte[] rlp = Leaf(records.Count, 1000);
            records.Add((Append(buffer, rlp), rlp));
        }

        ulong activeGeneration = GenerationOf(records[^1].Pointer);
        Assert.That(records.Select(record => Resolves(buffer, record)), Is.All.True, "everything resolves while the first generation is retained");

        middle!.Dispose();
        middle.Dispose();
        first.Dispose();
        AssertResolvesFrom(buffer, records, middleCopy!.Generation, "a copy keeps its generation and every later one after the original is disposed");

        middleCopy.Dispose();
        AssertResolvesFrom(buffer, records, activeGeneration - LiveGenerations + 1, "only the live generations resolve after the last release");

        using NodeRecordBufferPin oversizeRetain = buffer.PinHead();
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
        using NodeRecordBuffer buffer = new(GenerationBytes);
        using NodeRecordBufferPin retain = buffer.PinHead();
        while (GenerationOf(buffer.Reserve(GenerationBytes, out _, out _)) < LiveGenerations + 1) { }
        retain.Dispose();

        // Copy may have passed its disposed check before the releasing thread acquired the buffer lock.
        Assert.That(() => buffer.AddPin(retain.Generation), Throws.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void Root_index_returns_the_newest_matching_root([Values] bool storage)
    {
        using NodeRecordBuffer buffer = new(GenerationBytes);
        Hash256? address = storage ? TestItem.KeccakA : null;
        NodeRecordBufferPin retain = buffer.PinHead();
        ulong droppedPointer = Append(buffer, Leaf(-1, 8));

        ValueHash256[] roots = new ValueHash256[1500];
        ulong[] pointers = new ulong[roots.Length];
        for (int i = 0; i < roots.Length; i++)
        {
            roots[i] = Root(i);
            pointers[i] = Append(buffer, Leaf(i, 8));
            buffer.SetRoot(address, roots[i], pointers[i]);
        }

        Hash256 otherAddress = storage ? SameSlotAddress(address!) : Hash256.Zero;
        ulong otherAddressPointer = Append(buffer, Leaf(-2, 8));
        buffer.SetRoot(otherAddress, roots[0], otherAddressPointer);
        pointers[1] = Append(buffer, Leaf(-3, 8));
        buffer.SetRoot(address, roots[1], pointers[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roots.Select(root => buffer.FindRoot(address, root, ulong.MaxValue)), Is.EqualTo(pointers), "newest pointer of every root");
            Assert.That(buffer.FindRoot(otherAddress, roots[0], ulong.MaxValue), Is.EqualTo(otherAddressPointer), "the same root under another address");
            Assert.That(buffer.FindRoot(address, Root(-1), ulong.MaxValue), Is.Zero, "a root never set");
        }

        retain.Dispose();
        while (GenerationOf(Append(buffer, Leaf(0, 1000))) < GenerationOf(droppedPointer) + LiveGenerations) { }

        buffer.SetRoot(address, Root(-2), droppedPointer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.FindRoot(address, Root(-2), ulong.MaxValue), Is.Zero, "a root whose record dropped");
            Assert.That(buffer.FindRoot(address, roots[0], ulong.MaxValue), Is.Zero, "a root whose root key dropped");
        }
    }

    [Test]
    public void Concurrent_reservations_and_root_sets_are_disjoint_and_readable([Values] bool dropping)
    {
        const int threadCount = 16;
        const int recordsPerThread = 10_000;
        const int recordsPerRetain = 1_000;
        using NodeRecordBuffer buffer = new(GenerationBytes);
        using NodeRecordBufferPin? retain = dropping ? null : buffer.PinHead();
        ulong[][] pointers = new ulong[threadCount][];
        NodeRecordBufferPin?[] threadRetains = new NodeRecordBufferPin?[threadCount];

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
                bool retained = !dropping || i >= recordsPerThread - recordsPerRetain;
                using IDisposable pin = buffer.PinAll();
                bool located = buffer.TryLocate(pointer, out NodeRecord record);
                if (located ? !Matches(record, Leaf(seed, 8)) : retained) failures.Add($"record {seed}");
                ulong root = buffer.FindRoot(ConcurrentAddress(thread), Root(seed), ulong.MaxValue);
                if (root != pointer && (retained || root != 0)) failures.Add($"root {seed}");
            }
        }

        foreach (NodeRecordBufferPin? threadRetain in threadRetains) threadRetain!.Dispose();
        Assert.That(failures, Is.Empty);
    }

    private static Hash256 SameSlotAddress(Hash256 address)
    {
        byte[] bytes = address.BytesToArray();
        bytes[^1] ^= 1;
        return new Hash256(bytes);
    }

    private static Hash256? ConcurrentAddress(int thread) => thread % 2 == 0 ? null : Keccak.Compute(BitConverter.GetBytes(thread));

    public static ulong GenerationOf(ulong pointer) => pointer >> 32;

    private static ValueHash256 Root(int seed) => ValueKeccak.Compute(BitConverter.GetBytes(seed));

    private static Hash256 RecordAddress(int seed) => Keccak.Compute(BitConverter.GetBytes(seed));

    private static ulong Append(NodeRecordBuffer buffer, byte[] rlp) => AppendAt(buffer, null, rlp);

    private static ulong AppendAt(NodeRecordBuffer buffer, Hash256? address, byte[] rlp) => buffer.AppendRecordFromRlp(address, TreePath.Empty, ValueKeccak.Compute(rlp), rlp, []);

    public static byte[] Leaf(int seed, int valueLength)
    {
        byte[] value = new byte[valueLength];
        BinaryPrimitives.WriteInt32LittleEndian(value, seed);
        return Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes([1, 2], isLeaf: true)), Rlp.Encode(value)).Bytes;
    }

    /// <remarks>Retired native owners are freed in the background.</remarks>
    private static bool ThrowsDisposed(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static bool IsDisposed(NativeNodeBuffer buffer)
    {
        try
        {
            buffer.GetSpan();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    public static bool IsLocatable(NodeRecordBuffer buffer, ulong pointer)
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

    private static void AssertResolvesFrom(NodeRecordBuffer buffer, List<(ulong Pointer, byte[] Rlp)> records, ulong oldestReadable, string message) =>
        Assert.That(records.Select(record => Resolves(buffer, record)), Is.EqualTo(records.Select(record => GenerationOf(record.Pointer) >= oldestReadable)), message);
}
