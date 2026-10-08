// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Config;
using Nethermind.Core.Authentication;
using Nethermind.Core.Memory;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Runner.JsonRpc;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Runner.Test.JsonRpc;

public class TransportMemoryPoolTests
{
    private const int LargeCap = TransportMemoryPool.MaxLargeBlocks;
    private static readonly TimeSpan Slot = TimeSpan.FromSeconds(12);

    [Test]
    public void Rent_hands_out_a_whole_large_block_and_reuses_returned_ones([Values(-1, 1, 4096, TransportMemoryPool.BlockSize)] int minBufferSize)
    {
        using TransportMemoryPool pool = new();
        IMemoryOwner<byte> first = pool.Rent(minBufferSize);
        Memory<byte> firstMemory = first.Memory;
        first.Dispose();
        using IMemoryOwner<byte> second = pool.Rent(minBufferSize);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstMemory.Length, Is.EqualTo(TransportMemoryPool.BlockSize));
            Assert.That(second.Memory.Span == firstMemory.Span, Is.True, "a returned block is handed out again");
            Assert.That(pool.Large.RetainedBlocks, Is.Zero);
            Assert.That(pool.Large.BlocksAllocated, Is.EqualTo(1));
            Assert.That(pool.Small.BlocksAllocated, Is.Zero);
        }
    }

    [Test]
    public void Rent_beyond_a_large_block_is_served_outside_the_pool()
    {
        using TransportMemoryPool pool = new();
        IMemoryOwner<byte> large = pool.Rent(TransportMemoryPool.BlockSize + 1);
        int length = large.Memory.Length;
        large.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(length, Is.EqualTo(TransportMemoryPool.BlockSize + 1));
            Assert.That(pool.Large.Blocks + pool.Small.Blocks, Is.Zero);
            Assert.That(pool.Large.RetainedBlocks + pool.Small.RetainedBlocks, Is.Zero);
        }
    }

    [Test]
    public void A_block_goes_back_to_the_pool_once()
    {
        using TransportMemoryPool pool = new();
        IMemoryOwner<byte> owner = pool.Rent();
        owner.Dispose();
        owner.Dispose();

        using IMemoryOwner<byte> first = pool.Rent();
        using IMemoryOwner<byte> second = pool.Rent();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Memory.Span == second.Memory.Span, Is.False, "two owners must not share a block");
            Assert.That(pool.Large.RetainedBlocks, Is.Zero);
            Assert.That(pool.Large.BlocksInUse, Is.EqualTo(2));
        }
    }

    [Test]
    public void A_fan_out_takes_the_large_blocks_up_to_the_cap_and_small_ones_beyond()
    {
        // Thousands of small writes out at once, as when a notification goes to every subscriber.
        const int burst = 3_000;
        using TransportMemoryPool pool = new();

        List<IMemoryOwner<byte>> first = RentBlocks(pool, burst);
        int[] lengths = first.Select(static o => o.Memory.Length).ToArray();
        ReturnBlocks(first);
        ReturnBlocks(RentBlocks(pool, burst));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lengths.Count(static l => l == TransportMemoryPool.BlockSize), Is.EqualTo(LargeCap));
            Assert.That(lengths.Count(static l => l == TransportMemoryPool.SmallBlockSize), Is.EqualTo(burst - LargeCap));
            Assert.That(pool.Large.BlocksAllocated, Is.EqualTo(LargeCap), "the second burst reuses the large blocks");
            Assert.That(pool.Small.BlocksAllocated, Is.EqualTo(burst - LargeCap), "the second burst reuses the small blocks");
            Assert.That(pool.Large.RetainedBlocks, Is.EqualTo(LargeCap));
            Assert.That(pool.Small.RetainedBlocks, Is.EqualTo(burst - LargeCap));
        }
    }

    [Test]
    public void After_a_fan_out_the_small_blocks_are_trimmed_and_the_large_ones_follow_their_own_demand()
    {
        const int burst = 1_000;
        const int steady = LargeCap / 2;
        using TransportMemoryPool pool = new();
        ReturnBlocks(RentBlocks(pool, burst));

        List<int> smallAfterTrim = [];
        for (int trim = 0; trim < 100; trim++)
        {
            ReturnBlocks(RentBlocks(pool, steady));
            pool.Trim();
            smallAfterTrim.Add(pool.Small.RetainedBlocks);
        }

        int[] steps = smallAfterTrim.Zip(smallAfterTrim.Skip(1), static (before, after) => before - after).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(smallAfterTrim.Take(TransportMemoryPool.DemandWindows), Is.All.EqualTo(burst - LargeCap), "the burst is remembered for the whole demand window");
            Assert.That(smallAfterTrim[TransportMemoryPool.DemandWindows], Is.LessThan(burst - LargeCap), "released once the window has passed");
            Assert.That(steps, Is.All.InRange(0, (burst - LargeCap) / 20), "released a few at a time");
            Assert.That(pool.Small.RetainedBlocks, Is.EqualTo(TransportMemoryPool.MinRetainedBlocks));
            Assert.That(pool.Large.RetainedBlocks, Is.EqualTo(steady), "keeps the large blocks current demand still needs");
            Assert.That(pool.Large.BlocksAllocated, Is.EqualTo(LargeCap));
            Assert.That(pool.Small.BlocksAllocated, Is.EqualTo(burst - LargeCap));
        }
    }

    [Test]
    public void While_the_large_blocks_are_out_a_rent_above_a_small_block_gets_a_shared_array_returned_once()
    {
        // Not a bucket size, so the shared pool hands out a longer array than asked for.
        const int size = 3 * TransportMemoryPool.SmallBlockSize + 1;
        using TransportMemoryPool pool = new();
        List<IMemoryOwner<byte>> large = RentBlocks(pool, LargeCap);
        byte[] primed = ArrayPool<byte>.Shared.Rent(size);
        ArrayPool<byte>.Shared.Return(primed);

        IMemoryOwner<byte> shared = pool.Rent(size);
        bool fromSharedPool = MemoryMarshal.TryGetArray(shared.Memory, out ArraySegment<byte> segment) && ReferenceEquals(segment.Array, primed);
        int sharedLength = shared.Memory.Length;
        IMemoryOwner<byte> small = pool.Rent(TransportMemoryPool.SmallBlockSize);
        int smallLength = small.Memory.Length;
        shared.Dispose();
        shared.Dispose();
        small.Dispose();
        ReturnBlocks(large);

        byte[] first = ArrayPool<byte>.Shared.Rent(size);
        byte[] second = ArrayPool<byte>.Shared.Rent(size);
        ArrayPool<byte>.Shared.Return(second);
        ArrayPool<byte>.Shared.Return(first);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromSharedPool, Is.True, "the array did not come from the shared pool");
            Assert.That(sharedLength, Is.EqualTo(size), "the owner sees exactly the size it asked for");
            Assert.That(first, Is.SameAs(primed), "the array went back to the shared pool");
            Assert.That(second, Is.Not.SameAs(first), "the array went back to the shared pool twice");
            Assert.That(smallLength, Is.EqualTo(TransportMemoryPool.SmallBlockSize));
            Assert.That(pool.Large.Blocks, Is.EqualTo(LargeCap), "the cap holds");
            Assert.That(pool.Small.Blocks, Is.EqualTo(1));
            Assert.That(pool.Small.RetainedBlocks, Is.EqualTo(1), "the shared array is not pooled here");
        }
    }

    [Test]
    public void Pooled_blocks_pin_without_a_gc_handle_and_shared_arrays_with_one()
    {
        using TransportMemoryPool pool = new();
        List<IMemoryOwner<byte>> large = RentBlocks(pool, LargeCap);
        using IMemoryOwner<byte> small = pool.Rent();
        using IMemoryOwner<byte> shared = pool.Rent(2 * TransportMemoryPool.SmallBlockSize);

        bool[] pooledHandles = [.. new[] { large[0], small }.SelectMany(static o => new[] { PinsWithGcHandle(o.Memory), PinsWithGcHandle(o.Memory) })];
        bool sharedHandle = PinsWithGcHandle(shared.Memory);
        int smallLength = small.Memory.Length;
        ReturnBlocks(large);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(smallLength, Is.EqualTo(TransportMemoryPool.SmallBlockSize));
            Assert.That(pooledHandles, Is.All.False, "a pooled block is not handed out as pre-pinned memory");
            Assert.That(sharedHandle, Is.True, "an array that is not pinned was handed out as pre-pinned memory");
        }
    }

    [Test]
    public void Blocks_go_back_to_the_list_of_their_size()
    {
        const int small = 5;
        using TransportMemoryPool pool = new();
        ReturnBlocks(RentBlocks(pool, LargeCap + small));
        int largeRetained = pool.Large.RetainedBlocks;
        int smallRetained = pool.Small.RetainedBlocks;

        List<IMemoryOwner<byte>> again = RentBlocks(pool, LargeCap + small);
        int[] lengths = again.Select(static o => o.Memory.Length).ToArray();
        ReturnBlocks(again);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(largeRetained, Is.EqualTo(LargeCap));
            Assert.That(smallRetained, Is.EqualTo(small));
            Assert.That(lengths.Take(LargeCap), Is.All.EqualTo(TransportMemoryPool.BlockSize));
            Assert.That(lengths.Skip(LargeCap), Is.All.EqualTo(TransportMemoryPool.SmallBlockSize));
            Assert.That(pool.Large.BlocksAllocated + pool.Small.BlocksAllocated, Is.EqualTo(LargeCap + small));
        }
    }

    [Test]
    public void Sustained_demand_stops_allocating_after_warm_up()
    {
        const int demand = 100;
        using TransportMemoryPool pool = new();

        for (int round = 0; round < 10 * TransportMemoryPool.DemandWindows; round++)
        {
            List<IMemoryOwner<byte>> owners = RentBlocks(pool, demand);
            // Trims land at either end of a burst, as the timer would.
            if (round % 2 == 0) pool.Trim();
            ReturnBlocks(owners);
            if (round % 3 == 0) pool.Trim();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.Large.BlocksAllocated, Is.EqualTo(LargeCap), "blocks are reused once the pool has warmed up");
            Assert.That(pool.Small.BlocksAllocated, Is.EqualTo(demand - LargeCap), "blocks are reused once the pool has warmed up");
            Assert.That(pool.Large.BlocksReleased + pool.Small.BlocksReleased, Is.Zero);
        }
    }

    [Test]
    public void A_large_surplus_is_released_at_a_twentieth_per_trim()
    {
        const int surplus = 400;
        using TransportMemoryPool pool = new();
        ReturnBlocks(RentBlocks(pool, LargeCap + surplus));
        for (int trim = 0; trim < TransportMemoryPool.DemandWindows; trim++) pool.Trim();

        pool.Trim();

        Assert.That(pool.Small.RetainedBlocks, Is.EqualTo(surplus - surplus / 20));
    }

    [Test]
    public void An_idle_pool_trims_to_the_floor_on_the_factory_timer()
    {
        const int burst = 100;
        ManualTimeProvider time = new();
        using TransportMemoryPoolFactory factory = new(time);
        using TransportMemoryPool busy = (TransportMemoryPool)factory.Create();
        using TransportMemoryPool other = (TransportMemoryPool)factory.Create();
        ReturnBlocks(RentBlocks(busy, burst));
        ReturnBlocks(RentBlocks(other, burst));

        time.Advance(TransportMemoryPool.TrimInterval * TransportMemoryPool.DemandWindows);
        int smallThroughTheWindow = busy.Small.RetainedBlocks;
        int largeThroughTheWindow = busy.Large.RetainedBlocks;
        time.Advance(TransportMemoryPool.TrimInterval);
        int smallAfterTheWindow = busy.Small.RetainedBlocks;
        time.Advance(TimeSpan.FromMinutes(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(smallThroughTheWindow, Is.EqualTo(burst - LargeCap));
            Assert.That(largeThroughTheWindow, Is.EqualTo(LargeCap));
            Assert.That(smallAfterTheWindow, Is.InRange(burst - LargeCap - TransportMemoryPool.MinReleasePerTrim, burst - LargeCap - 1));
            foreach (TransportMemoryPool pool in new[] { busy, other })
            {
                Assert.That(pool.Large.RetainedBlocks, Is.EqualTo(TransportMemoryPool.MinRetainedBlocks), "every live pool is trimmed");
                Assert.That(pool.Small.RetainedBlocks, Is.EqualTo(TransportMemoryPool.MinRetainedBlocks), "every live pool is trimmed");
            }
        }
    }

    [Test]
    public void The_engine_pattern_keeps_its_large_blocks_between_slots([Values(5, 14, 22)] int blocksPerPayload)
    {
        ManualTimeProvider time = new();
        using TransportMemoryPoolFactory factory = new(time);
        using TransportMemoryPool pool = (TransportMemoryPool)factory.Create();
        bool allLarge = true;

        // An hour of slots, each payload held well under a millisecond; every tenth slot is missed.
        for (int slot = 0; slot < 300; slot++)
        {
            if (slot % 10 != 9)
            {
                List<IMemoryOwner<byte>> payload = RentBlocks(pool, blocksPerPayload);
                allLarge &= payload.All(static o => o.Memory.Length == TransportMemoryPool.BlockSize);
                ReturnBlocks(payload);
            }

            time.Advance(Slot);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allLarge, Is.True, "a payload body stays on large blocks");
            Assert.That(pool.Large.BlocksAllocated, Is.EqualTo(blocksPerPayload), "the blocks of one payload serve the next");
            Assert.That(pool.Large.BlocksReleased, Is.Zero);
            Assert.That(pool.Large.RetainedBlocks, Is.EqualTo(blocksPerPayload));
            Assert.That(pool.Small.BlocksAllocated, Is.Zero);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public void Concurrent_rents_and_returns_of_both_sizes_keep_blocks_exclusive_and_counts_consistent()
    {
        const int workers = 16;
        const int rounds = 3_000;
        const int maxHeld = 8;
        int[] sizes = [-1, TransportMemoryPool.SmallBlockSize, 2 * TransportMemoryPool.SmallBlockSize, TransportMemoryPool.BlockSize];
        using TransportMemoryPool pool = new();
        using CancellationTokenSource trimming = new();
        int clashes = 0;
        int shortBlocks = 0;
        int largeBlocksSeen = 0;
        int smallBlocksSeen = 0;

        Task trimmer = Task.Factory.StartNew(() =>
        {
            while (!trimming.IsCancellationRequested)
            {
                pool.Trim();
                Thread.Yield();
            }
        }, TaskCreationOptions.LongRunning);

        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
        {
            Random random = new(worker);
            List<IMemoryOwner<byte>> owners = [];
            for (int round = 0; round < rounds; round++)
            {
                long stamp = ((long)worker << 32) | (uint)round;
                int count = random.Next(1, maxHeld + 1);
                for (int i = 0; i < count; i++)
                {
                    int size = sizes[random.Next(sizes.Length)];
                    IMemoryOwner<byte> owner = pool.Rent(size);
                    Span<byte> span = owner.Memory.Span;
                    if (span.Length < Math.Max(size, TransportMemoryPool.SmallBlockSize)) Interlocked.Increment(ref shortBlocks);
                    if (span.Length == TransportMemoryPool.BlockSize) Interlocked.Increment(ref largeBlocksSeen);
                    if (span.Length == TransportMemoryPool.SmallBlockSize) Interlocked.Increment(ref smallBlocksSeen);
                    MemoryMarshal.Write(span, in stamp);
                    MemoryMarshal.Write(span[^sizeof(long)..], in stamp);
                    owners.Add(owner);
                }

                Thread.Yield();
                foreach (IMemoryOwner<byte> owner in owners)
                {
                    Span<byte> span = owner.Memory.Span;
                    if (MemoryMarshal.Read<long>(span) != stamp || MemoryMarshal.Read<long>(span[^sizeof(long)..]) != stamp)
                    {
                        Interlocked.Increment(ref clashes);
                    }

                    owner.Dispose();
                    // A late second return must not put the block back twice.
                    if (round % 7 == 0) owner.Dispose();
                }

                owners.Clear();
            }
        });

        trimming.Cancel();
        trimmer.Wait();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clashes, Is.Zero, "a block was handed to two owners at once");
            Assert.That(shortBlocks, Is.Zero, "a block was smaller than asked for");
            Assert.That(largeBlocksSeen, Is.GreaterThan(0));
            Assert.That(smallBlocksSeen, Is.GreaterThan(0), "the large blocks never ran out; the test needs more held at once");
            foreach (TransportMemoryPool.BlockList list in new[] { pool.Large, pool.Small })
            {
                Assert.That(list.BlocksInUse, Is.Zero);
                Assert.That(list.Blocks, Is.EqualTo(list.RetainedBlocks), "every block is retained, released or in use");
                Assert.That(list.BlocksAllocated - list.BlocksReleased, Is.EqualTo(list.RetainedBlocks));
            }

            Assert.That(pool.Large.Blocks, Is.LessThanOrEqualTo(LargeCap));
        }
    }

    [Test]
    public void Disposing_the_last_pool_stops_the_timer_and_a_new_pool_restarts_it()
    {
        ManualTimeProvider time = new();
        using TransportMemoryPoolFactory factory = new(time);
        bool idleBeforeAnyPool = factory.IsTrimming;
        MemoryPool<byte> first = factory.Create();
        MemoryPool<byte> second = factory.Create();
        int timersForTwoPools = time.ActiveTimers;

        first.Dispose();
        bool trimmingWithOnePool = factory.IsTrimming;
        second.Dispose();
        bool trimmingWithNone = factory.IsTrimming;
        int timersWithNone = time.ActiveTimers;

        using MemoryPool<byte> third = factory.Create();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(idleBeforeAnyPool, Is.False);
            Assert.That(timersForTwoPools, Is.EqualTo(1), "one timer serves every pool");
            Assert.That(trimmingWithOnePool, Is.True);
            Assert.That(trimmingWithNone, Is.False);
            Assert.That(timersWithNone, Is.Zero);
            Assert.That(factory.IsTrimming, Is.True);
            Assert.That(factory.LivePools, Is.EqualTo(1));
        }
    }

    [Test]
    public void A_disposed_factory_stops_trimming_its_pools()
    {
        ManualTimeProvider time = new();
        TransportMemoryPoolFactory factory = new(time);
        using TransportMemoryPool pool = (TransportMemoryPool)factory.Create();
        ReturnBlocks(RentBlocks(pool, 50));

        factory.Dispose();
        time.Advance(TimeSpan.FromMinutes(10));
        using TransportMemoryPool late = (TransportMemoryPool)factory.Create();
        using IMemoryOwner<byte> rented = late.Rent();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(time.ActiveTimers, Is.Zero);
            Assert.That(pool.Large.RetainedBlocks + pool.Small.RetainedBlocks, Is.EqualTo(50));
            Assert.That(factory.IsTrimming, Is.False, "a pool created after disposal does not restart the timer");
            Assert.That(rented.Memory.Length, Is.EqualTo(TransportMemoryPool.BlockSize));
        }
    }

    [Test]
    public void A_disposed_pool_refuses_rents_drops_returns_and_ignores_trims()
    {
        TransportMemoryPool pool = new();
        IMemoryOwner<byte> owner = pool.Rent();
        pool.Rent().Dispose();
        pool.Dispose();
        owner.Dispose();
        pool.Trim();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.Large.RetainedBlocks + pool.Small.RetainedBlocks, Is.Zero);
            Assert.That(pool.Large.BlocksReleased, Is.Zero);
            Assert.That(() => pool.Rent(), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(60_000)]
    public async Task Startup_gives_kestrel_the_pool_and_large_bodies_cross_it_intact()
    {
        using GCKeeper gcKeeper = new(NoGCStrategy.Instance, LimboLogs.Instance);
        WebApplication app = BuildHost(gcKeeper, endpoints: 1, ioQueues: null, EchoAsync);
        await app.StartAsync();
        TransportMemoryPoolFactory ours = app.Services.GetRequiredService<IMemoryPoolFactory<byte>>() as TransportMemoryPoolFactory;
        bool trimmingWhileServing = ours?.IsTrimming ?? false;

        byte[] sent = RandomBody(5 * TransportMemoryPool.BlockSize + 123, 42);
        using HttpClient client = new();
        using HttpResponseMessage response = await client.PostAsync(Addresses(app)[0], new ByteArrayContent(sent));
        byte[] received = await response.Content.ReadAsByteArrayAsync();
        await app.StopAsync();
        // Kestrel disposes its pools when it stops, and the last one stops the timer.
        bool trimmingAfterStop = ours?.IsTrimming ?? true;
        await app.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ours, Is.Not.Null, "Startup did not register the factory");
            Assert.That(ours?.PoolsCreated, Is.GreaterThan(0), "Kestrel did not take its pools from the factory");
            Assert.That(trimmingWhileServing, Is.True);
            Assert.That(trimmingAfterStop, Is.False, "the timer outlived Kestrel's pools");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(received, Is.EqualTo(sent));
        }
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(120_000)]
    public async Task A_busy_endpoint_cannot_take_the_large_blocks_of_another()
    {
        const int ioQueues = 2;
        // More connections per I/O queue than a pool has large blocks, each leaving its body unread.
        const int busyConnections = ioQueues * (LargeCap + 8);
        using GCKeeper gcKeeper = new(NoGCStrategy.Instance, LimboLogs.Instance);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int busyPort = 0;
        WebApplication app = BuildHost(gcKeeper, endpoints: 2, ioQueues, async ctx =>
        {
            if (ctx.Connection.LocalPort == Volatile.Read(ref busyPort)) await release.Task;
            await EchoAsync(ctx);
        });
        await app.StartAsync();
        TransportMemoryPoolFactory factory = (TransportMemoryPoolFactory)app.Services.GetRequiredService<IMemoryPoolFactory<byte>>();
        Uri[] addresses = Addresses(app);
        Volatile.Write(ref busyPort, addresses[0].Port);

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(90) };
        byte[][] busyBodies = Enumerable.Range(0, busyConnections).Select(static i => RandomBody(2 * TransportMemoryPool.BlockSize + i, i)).ToArray();
        Task<byte[]>[] busy = busyBodies.Select(body => PostAsync(client, addresses[0], body)).ToArray();

        // Wait until the busy endpoint's pools have every large block out and have moved on to small ones.
        TransportMemoryPool[] busyPools = [];
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (busyPools.Length < ioQueues && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            busyPools = factory.Pools.Where(static p => p.Large.BlocksInUse == LargeCap && p.Small.BlocksInUse > 0).ToArray();
        }

        TransportMemoryPool[] quietPools = factory.Pools.Except(busyPools).ToArray();
        long quietSmallBefore = quietPools.Sum(static p => p.Small.BlocksAllocated);
        long quietLargeBefore = quietPools.Sum(static p => p.Large.BlocksAllocated);

        byte[] payload = RandomBody(5 * TransportMemoryPool.BlockSize + 123, 7);
        byte[] payloadEcho = await PostAsync(client, addresses[1], payload);
        bool busyStillFull = busyPools.All(static p => p.Large.BlocksInUse == LargeCap);
        long quietSmallAfter = quietPools.Sum(static p => p.Small.BlocksAllocated);
        long quietLargeAfter = quietPools.Sum(static p => p.Large.BlocksAllocated);

        release.SetResult();
        byte[][] busyEchoes = await Task.WhenAll(busy);
        long busySmallAllocated = busyPools.Sum(static p => p.Small.BlocksAllocated);
        int poolsCreated = factory.PoolsCreated;
        await app.StopAsync();
        await app.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(poolsCreated, Is.EqualTo(2 * ioQueues), "Kestrel gives each endpoint pools of its own");
            Assert.That(busyPools, Has.Length.EqualTo(ioQueues), "the busy endpoint's pools did not run out of large blocks");
            Assert.That(busyStillFull, Is.True);
            Assert.That(quietPools, Has.Length.EqualTo(ioQueues));
            Assert.That(quietSmallBefore, Is.Zero);
            Assert.That(quietSmallAfter, Is.Zero, "the payload was received in small blocks");
            Assert.That(quietLargeAfter, Is.GreaterThan(quietLargeBefore), "the payload was received in the quiet endpoint's large blocks");
            Assert.That(busySmallAllocated, Is.GreaterThan(0));
            Assert.That(payloadEcho, Is.EqualTo(payload));
            for (int i = 0; i < busyConnections; i++) Assert.That(busyEchoes[i], Is.EqualTo(busyBodies[i]), $"busy body {i}");
        }
    }

    private static bool PinsWithGcHandle(Memory<byte> memory)
    {
        FieldInfo handleField = typeof(MemoryHandle).GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MemoryHandle no longer keeps its GCHandle in _handle");
        using MemoryHandle handle = memory.Pin();
        return ((GCHandle)handleField.GetValue(handle)).IsAllocated;
    }

    private static WebApplication BuildHost(GCKeeper gcKeeper, int endpoints, int? ioQueues, RequestDelegate handler)
    {
        JsonRpcConfig rpcConfig = new();
        RpcModuleProvider moduleProvider = new(new RealFileSystem(), rpcConfig, new EthereumJsonSerializer(), LimboLogs.Instance);
        Bootstrap.Instance.JsonRpcService = new JsonRpcService(moduleProvider, LimboLogs.Instance, rpcConfig, gcKeeper);
        Bootstrap.Instance.LogManager = LimboLogs.Instance;
        Bootstrap.Instance.JsonSerializer = new EthereumJsonSerializer();
        Bootstrap.Instance.JsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
        Bootstrap.Instance.JsonRpcAuthentication = Substitute.For<IRpcAuthentication>();
        IConfigProvider configProvider = Substitute.For<IConfigProvider>();
        configProvider.GetConfig<IJsonRpcConfig>().Returns(rpcConfig);

        // In the order JsonRpcRunner builds its host: Kestrel first, then Startup's services.
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost
            .UseKestrelCore()
            .ConfigureKestrel(options =>
            {
                for (int i = 0; i < endpoints; i++) options.Listen(IPAddress.Loopback, 0);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(configProvider);
                new Startup().ConfigureServices(services);
            });
        if (ioQueues is { } queues) builder.WebHost.UseSockets(options => options.IOQueueCount = queues);

        WebApplication app = builder.Build();
        app.Run(handler);
        return app;
    }

    private static async Task EchoAsync(HttpContext ctx)
    {
        using MemoryStream body = new();
        await ctx.Request.Body.CopyToAsync(body);
        ctx.Response.ContentLength = body.Length;
        await ctx.Response.Body.WriteAsync(body.GetBuffer().AsMemory(0, (int)body.Length));
    }

    private static Uri[] Addresses(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(static a => new Uri(a)).ToArray();

    private static async Task<byte[]> PostAsync(HttpClient client, Uri address, byte[] body)
    {
        using HttpResponseMessage response = await client.PostAsync(address, new ByteArrayContent(body));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static byte[] RandomBody(int length, int seed)
    {
        byte[] body = new byte[length];
        new Random(seed).NextBytes(body);
        return body;
    }

    private static List<IMemoryOwner<byte>> RentBlocks(MemoryPool<byte> pool, int count)
    {
        List<IMemoryOwner<byte>> owners = new(count);
        for (int i = 0; i < count; i++) owners.Add(pool.Rent());
        return owners;
    }

    private static void ReturnBlocks(List<IMemoryOwner<byte>> owners)
    {
        foreach (IMemoryOwner<byte> owner in owners) owner.Dispose();
    }

    /// <summary>Fires timers only when the test advances the clock, in due order, on the test's thread.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public int ActiveTimers => _timers.Count(static t => t.IsActive);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            timer.Due = _now + dueTime;
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset end = _now + by;
            while (_timers.Where(t => t.IsActive && t.Due <= end).MinBy(static t => t.Due) is { } next)
            {
                _now = next.Due;
                next.Fire(_now);
            }

            _now = end;
        }

        private sealed class ManualTimer(TimerCallback callback, object state) : ITimer
        {
            private TimeSpan _period;
            private bool _disposed;
            private bool _armed;

            public DateTimeOffset Due { get; set; }

            public bool IsActive => !_disposed && _armed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _armed = dueTime != Timeout.InfiniteTimeSpan;
                _period = period;
                return true;
            }

            public void Fire(DateTimeOffset now)
            {
                if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan) Due = now + _period;
                else _armed = false;
                callback(state);
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
