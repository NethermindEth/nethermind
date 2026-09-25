// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Buffers.Slab;
using NUnit.Framework;

namespace Nethermind.Core.Test.Buffers.Slab;

[Parallelizable(ParallelScope.All)]
public unsafe class SlabMemoryAllocatorTests
{
    private const int PageSize = 4096;
    private const int Quantum = 16;
    private const int ThreadCacheMaxCount = 8;

    private static SlabMemoryAllocator CreateAllocator() =>
        new(new SlabAllocatorOptions(SlabAllocatorOptions.GenerateSizeClasses(Quantum, 4, PageSize), PageSize, Quantum, ThreadCacheMaxCount));

    [TestCase(64, 4, 2048, new[] { 64, 128, 192, 256, 320, 384, 448, 512, 640, 768, 896, 1024, 1280, 1536, 1792, 2048 })]
    [TestCase(16, 4, 128, new[] { 16, 32, 48, 64, 80, 96, 112, 128 })]
    [TestCase(16, 2, 200, new[] { 16, 32, 48, 64, 96, 128, 192 })]
    public void GenerateSizeClasses_spaces_classes_like_jemalloc(int quantum, int classesPerDoubling, int maxSize, int[] expected) =>
        Assert.That(SlabAllocatorOptions.GenerateSizeClasses(quantum, classesPerDoubling, maxSize), Is.EqualTo(expected));

    [Test]
    public void Options_reject_invalid_geometry()
    {
        int[] classes = [16, 32];
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SlabAllocatorOptions(classes, 3000, Quantum, 8), "page size not a power of two");
            Assert.Throws<ArgumentOutOfRangeException>(() => new SlabAllocatorOptions(classes, PageSize, 8, 8), "quantum below minimum");
            Assert.Throws<ArgumentOutOfRangeException>(() => new SlabAllocatorOptions([32, 16], PageSize, Quantum, 8), "classes not ascending");
            Assert.Throws<ArgumentOutOfRangeException>(() => new SlabAllocatorOptions([24], PageSize, Quantum, 8), "class not a quantum multiple");
            Assert.Throws<ArgumentOutOfRangeException>(() => new SlabAllocatorOptions([], PageSize, Quantum, 8), "no classes");
            Assert.Throws<ArgumentOutOfRangeException>(() => SlabAllocatorOptions.GenerateSizeClasses(Quantum, 3, PageSize), "classes per doubling not a power of two");
        }
    }

    [Test]
    public void Allocate_sizes_blocks_to_their_class_and_frees_them_completely([Values(0, 1, 16, 1000, 1200, PageSize, 5000, 20000, 200_000)] int size)
    {
        using SlabMemoryAllocator allocator = CreateAllocator();
        SlabAllocation first = allocator.Allocate(size);
        SlabAllocation second = allocator.Allocate(size);
        BlockSpan(first).Fill(0xA1);
        BlockSpan(second).Fill(0xB2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Capacity, Is.EqualTo(allocator.RoundUpCapacity(size)).And.GreaterThanOrEqualTo(size));
            Assert.That(first.Pointer != second.Pointer, "blocks are distinct");
            Assert.That(BlockSpan(first).IndexOfAnyExcept((byte)0xA1), Is.EqualTo(-1), "the second fill did not touch the first block");
            Assert.That(allocator.OutstandingBytes, Is.GreaterThanOrEqualTo(2L * first.Capacity), "a refill batch may sit in the thread cache");
        }

        allocator.Free(in first);
        allocator.Free(in second);
        allocator.FlushThreadCache();
        Assert.That(allocator.OutstandingBytes, Is.Zero);
    }

    [Test]
    public void Thread_cache_reuses_the_last_freed_region_and_refills_without_growing()
    {
        using SlabMemoryAllocator allocator = CreateAllocator();
        const int size = 64;
        int cacheCapacity = Math.Clamp(64 * 1024 / size, 4, ThreadCacheMaxCount);
        int count = 3 * cacheCapacity;
        SlabAllocation[] blocks = new SlabAllocation[count];
        for (int i = 0; i < count; i++)
        {
            blocks[i] = allocator.Allocate(size);
            BlockSpan(blocks[i]).Fill((byte)i);
        }

        for (int i = 0; i < count; i++)
            Assert.That(BlockSpan(blocks[i]).IndexOfAnyExcept((byte)i), Is.EqualTo(-1), $"region {i} overlaps another");

        long reserved = allocator.ReservedBytes;
        for (int i = 0; i < count; i++) allocator.Free(in blocks[i]);
        SlabAllocation reused = allocator.Allocate(size);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reused.Pointer == blocks[^1].Pointer, "the thread cache is LIFO");
            Assert.That(allocator.ReservedBytes, Is.EqualTo(reserved), "cached regions do not grow the arena");
        }

        allocator.Free(in reused);
        allocator.FlushThreadCache();
        Assert.That(allocator.OutstandingBytes, Is.Zero);
    }

    [Test]
    public void Empty_slabs_are_released_beyond_one_spare()
    {
        using SlabMemoryAllocator allocator = CreateAllocator();
        // A page-sized class puts one region per slab, so every region is its own native block.
        SlabAllocation[] blocks = new SlabAllocation[33];
        for (int i = 0; i < blocks.Length; i++) blocks[i] = allocator.Allocate(PageSize);
        Assert.That(allocator.ReservedBytes, Is.GreaterThanOrEqualTo(blocks.Length * (long)PageSize), "a refill batch may add a few more slabs");

        for (int i = 0; i < blocks.Length; i++) allocator.Free(in blocks[i]);
        allocator.FlushThreadCache();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocator.OutstandingBytes, Is.Zero);
            Assert.That(allocator.ReservedBytes, Is.EqualTo(PageSize), "one empty slab is kept spare");
        }

        allocator.Dispose();
        Assert.That(allocator.ReservedBytes, Is.Zero);
    }

    [Test]
    public void Bins_allocate_from_the_fullest_slab_first()
    {
        using SlabMemoryAllocator allocator = CreateAllocator();
        // Four regions per slab, so sixteen regions fill exactly four slabs.
        SizeClassBin bin = new(allocator, 0, PageSize / 4, PageSize, 8);
        RegionHandle[] handles = new RegionHandle[16];
        Assert.That(bin.AllocateBatch(handles), Is.EqualTo(16));
        Nethermind.Core.Buffers.Slab.Slab[] slabs = [handles[0].Slab, handles[4].Slab, handles[8].Slab, handles[12].Slab];
        Assert.That(slabs, Is.Unique);

        // Leave slab 0 with three free regions, slab 1 with one, slab 2 with two and slab 3 full.
        bin.FreeBatch([handles[0], handles[1], handles[2], handles[4], handles[8], handles[9]]);

        RegionHandle[] next = new RegionHandle[4];
        bin.AllocateBatch(next);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(next[0].Slab, Is.SameAs(slabs[1]), "one free region: the fullest");
            Assert.That(next[1].Slab, Is.SameAs(slabs[2]), "two free regions");
            Assert.That(next[2].Slab, Is.SameAs(slabs[2]));
            Assert.That(next[3].Slab, Is.SameAs(slabs[0]), "three free regions: the emptiest, last");
        }

        // Draining a slab fully retires it: the first becomes the spare, the second is released.
        long reserved = bin.SlabBytes;
        bin.FreeBatch([handles[12], handles[13], handles[14], handles[15]]);
        Assert.That(bin.SlabBytes, Is.EqualTo(reserved), "the first empty slab is kept spare");
        bin.FreeBatch([handles[5], handles[6], handles[7], next[0]]);
        Assert.That(bin.SlabBytes, Is.EqualTo(reserved - PageSize), "the second empty slab is released");
    }

    [Test]
    public void An_idle_thread_cache_is_flushed_by_the_second_sweep()
    {
        using SlabMemoryAllocator allocator = CreateAllocator();
        // A private cache, so the allocator's own gen-2 sweeps cannot interleave with the calls below.
        SizeClassBin bin = new(allocator, 0, 128, PageSize, 8);
        SlabThreadCache cache = new([bin]);
        RegionHandle[] handles = new RegionHandle[4];
        for (int i = 0; i < handles.Length; i++) handles[i] = cache.Rent(0);
        for (int i = 0; i < handles.Length; i++) cache.Return(0, in handles[i]);
        long parked = bin.AllocatedBytes;
        Assert.That(parked, Is.EqualTo(4 * 128L), "the regions sit in the cache");

        cache.TrimIfIdle();
        Assert.That(bin.AllocatedBytes, Is.EqualTo(parked), "the first sweep only arms a cache that was in use");
        cache.Return(0, cache.Rent(0));
        cache.TrimIfIdle();
        Assert.That(bin.AllocatedBytes, Is.EqualTo(parked), "a cache used since the last sweep is kept");
        cache.TrimIfIdle();
        Assert.That(bin.AllocatedBytes, Is.Zero, "an untouched cache is flushed");
    }

    [Test]
    public void Other_threads_caches_are_flushed_on_demand_and_by_the_gen2_sweep()
    {
        using SlabMemoryAllocator allocator = CreateAllocator();
        using ManualResetEventSlim parked = new();
        using ManualResetEventSlim release = new();
        Thread worker = new(() =>
        {
            SlabAllocation block = allocator.Allocate(128);
            allocator.Free(in block);
            parked.Set();
            release.Wait();
        })
        { IsBackground = true };
        worker.Start();
        parked.Wait();
        Assert.That(allocator.OutstandingBytes, Is.GreaterThan(0), "the worker's refill batch sits in its cache");
        allocator.FlushAllThreadCaches();
        Assert.That(allocator.OutstandingBytes, Is.Zero, "a live thread's cache is flushed from another thread");

        SlabAllocation again = allocator.Allocate(128);
        allocator.Free(in again);
        // The sweeper re-registers its finalizer, so consecutive collections arm and then flush the cache.
        for (int attempt = 0; attempt < 5 && allocator.OutstandingBytes > 0; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.That(allocator.OutstandingBytes, Is.Zero, "the gen-2 sweep flushed this thread's idle cache");
        release.Set();
        worker.Join();
    }

    [Test]
    public void A_dying_thread_returns_its_cached_regions()
    {
        using SlabMemoryAllocator allocator = CreateAllocator();
        Thread worker = new(() =>
        {
            SlabAllocation[] blocks = new SlabAllocation[4];
            for (int i = 0; i < blocks.Length; i++) blocks[i] = allocator.Allocate(PageSize / blocks.Length);
            for (int i = 0; i < blocks.Length; i++) allocator.Free(in blocks[i]);
        });
        worker.Start();
        worker.Join();
        Assert.That(allocator.OutstandingBytes, Is.GreaterThan(0), "the regions are parked in the dead thread's cache");

        // The thread-local slot is unlinked by one collection and the cache finalized by a later one.
        for (int attempt = 0; attempt < 10 && allocator.OutstandingBytes > 0; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.That(allocator.OutstandingBytes, Is.Zero);

        // The cache held every region of its slab, so the slab was unreachable too and its finalizer must not free
        // the block the flush handed back to the bin.
        SlabAllocation again = allocator.Allocate(PageSize / 4);
        Assert.That(((Core.Buffers.Slab.Slab)again.Owner!).IsReleased, Is.False, "the flushed slab is handed out again");
        allocator.Free(in again);
    }

    [Test]
    public void Dispose_frees_slabs_once_nothing_is_outstanding([Values] bool freeBeforeDispose)
    {
        SlabMemoryAllocator allocator = CreateAllocator();
        SlabAllocation small = allocator.Allocate(100);
        SlabAllocation large = allocator.Allocate(3 * PageSize);

        if (freeBeforeDispose)
        {
            allocator.Free(in small);
            allocator.Free(in large);
            allocator.Dispose();
            Assert.That(allocator.ReservedBytes, Is.Zero, "the caller's cache is flushed by Dispose");
        }
        else
        {
            allocator.Dispose();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(allocator.ReservedBytes, Is.GreaterThanOrEqualTo(small.Capacity + large.Capacity), "live blocks keep their memory");
                Assert.Throws<ObjectDisposedException>(() => allocator.Allocate(1));
            }

            allocator.Free(in small);
            allocator.Free(in large);
            Assert.That(allocator.ReservedBytes, Is.Zero, "the last free releases the slab");
        }
    }

    [TestCase(8, 20_000, 1)]
    public void Concurrent_allocations_never_overlap_and_all_return(int threads, int iterations, int seed)
    {
        using SlabMemoryAllocator allocator = CreateAllocator();
        ConcurrentQueue<(SlabAllocation Block, byte Tag)> handedOff = new();
        List<Exception> failures = [];
        Thread[] workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int threadSeed = seed * 1000 + t;
            workers[t] = new Thread(() =>
            {
                try
                {
                    Random random = new(threadSeed);
                    List<(SlabAllocation Block, byte Tag)> mine = [];
                    for (int i = 0; i < iterations; i++)
                    {
                        byte tag = (byte)random.Next(1, 256);
                        SlabAllocation block = allocator.Allocate(random.Next(0, 3 * PageSize));
                        BlockSpan(block).Fill(tag);
                        if (random.Next(2) == 0) mine.Add((block, tag));
                        else handedOff.Enqueue((block, tag));

                        if (mine.Count > 32)
                        {
                            VerifyAndFree(allocator, mine[^1]);
                            mine.RemoveAt(mine.Count - 1);
                        }

                        if (i % 8 == 0 && handedOff.TryDequeue(out (SlabAllocation Block, byte Tag) foreign)) VerifyAndFree(allocator, foreign);
                    }

                    foreach ((SlabAllocation Block, byte Tag) entry in mine) VerifyAndFree(allocator, entry);
                    allocator.FlushThreadCache();
                }
                catch (Exception e)
                {
                    lock (failures) failures.Add(e);
                }
            });
            workers[t].Start();
        }

        foreach (Thread worker in workers) worker.Join();
        while (handedOff.TryDequeue(out (SlabAllocation Block, byte Tag) leftover)) VerifyAndFree(allocator, leftover);
        allocator.FlushThreadCache();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failures, Is.Empty);
            Assert.That(allocator.OutstandingBytes, Is.Zero);
        }
    }

    private static void VerifyAndFree(SlabMemoryAllocator allocator, (SlabAllocation Block, byte Tag) entry)
    {
        if (BlockSpan(entry.Block).IndexOfAnyExcept(entry.Tag) >= 0) throw new InvalidOperationException("a block was overwritten by another allocation");
        allocator.Free(in entry.Block);
    }

    private static Span<byte> BlockSpan(in SlabAllocation block) => new(block.Pointer, block.Capacity);
}
