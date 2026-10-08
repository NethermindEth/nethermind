// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    private static readonly TimeSpan Slot = TimeSpan.FromSeconds(12);

    [Test]
    public void Rent_hands_out_a_whole_block_and_reuses_returned_ones([Values(-1, 1, 4096, TransportMemoryPool.BlockSize)] int minBufferSize)
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
            Assert.That(pool.RetainedBlocks, Is.Zero);
            Assert.That(pool.BlocksAllocated, Is.EqualTo(1));
        }
    }

    [Test]
    public void Rent_beyond_a_block_is_served_outside_the_pool()
    {
        using TransportMemoryPool pool = new();
        IMemoryOwner<byte> large = pool.Rent(TransportMemoryPool.BlockSize + 1);
        int length = large.Memory.Length;
        large.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(length, Is.EqualTo(TransportMemoryPool.BlockSize + 1));
            Assert.That(pool.RetainedBlocks, Is.Zero);
            Assert.That(pool.BlocksInUse, Is.Zero);
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
            Assert.That(pool.RetainedBlocks, Is.Zero);
            Assert.That(pool.BlocksInUse, Is.EqualTo(2));
        }
    }

    [Test]
    public void Sustained_demand_stops_allocating_after_warm_up()
    {
        // Well above the 32 blocks the pool used to keep at most.
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
            Assert.That(pool.BlocksAllocated, Is.EqualTo(demand), "blocks are reused once the pool has warmed up");
            Assert.That(pool.BlocksReleased, Is.Zero);
            Assert.That(pool.RetainedBlocks, Is.EqualTo(demand));
        }
    }

    [Test]
    public void After_demand_drops_trimming_is_gradual_and_stops_at_the_remaining_demand()
    {
        const int peak = 200;
        const int remaining = 20;
        using TransportMemoryPool pool = new();
        ReturnBlocks(RentBlocks(pool, peak));

        List<int> retainedAfterTrim = [];
        for (int trim = 0; trim < 60; trim++)
        {
            ReturnBlocks(RentBlocks(pool, remaining));
            pool.Trim();
            retainedAfterTrim.Add(pool.RetainedBlocks);
        }

        int[] heldForTheWindow = retainedAfterTrim.Take(TransportMemoryPool.DemandWindows).ToArray();
        int[] steps = retainedAfterTrim.Zip(retainedAfterTrim.Skip(1), static (before, after) => before - after).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldForTheWindow, Is.All.EqualTo(peak), "the burst is remembered for the whole demand window");
            Assert.That(steps, Is.All.InRange(0, TransportMemoryPool.MinReleasePerTrim), "released a few at a time");
            Assert.That(retainedAfterTrim[TransportMemoryPool.DemandWindows], Is.LessThan(peak), "released once the window has passed");
            Assert.That(retainedAfterTrim[^1], Is.EqualTo(remaining), "keeps what current demand still needs");
            Assert.That(pool.BlocksAllocated, Is.EqualTo(peak));
            Assert.That(pool.BlocksReleased, Is.EqualTo(peak - remaining));
        }
    }

    [Test]
    public void A_large_surplus_is_released_at_a_twentieth_per_trim()
    {
        const int peak = 400;
        using TransportMemoryPool pool = new();
        ReturnBlocks(RentBlocks(pool, peak));
        for (int trim = 0; trim < TransportMemoryPool.DemandWindows; trim++) pool.Trim();

        pool.Trim();

        Assert.That(pool.RetainedBlocks, Is.EqualTo(peak - peak / 20));
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
        int retainedThroughTheWindow = busy.RetainedBlocks;
        time.Advance(TransportMemoryPool.TrimInterval);
        int retainedAfterTheWindow = busy.RetainedBlocks;
        time.Advance(TimeSpan.FromMinutes(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retainedThroughTheWindow, Is.EqualTo(burst));
            Assert.That(retainedAfterTheWindow, Is.InRange(burst - TransportMemoryPool.MinReleasePerTrim, burst - 1));
            Assert.That(busy.RetainedBlocks, Is.EqualTo(TransportMemoryPool.MinRetainedBlocks));
            Assert.That(other.RetainedBlocks, Is.EqualTo(TransportMemoryPool.MinRetainedBlocks), "every live pool is trimmed");
        }
    }

    [Test]
    public void The_engine_pattern_keeps_its_blocks_between_slots([Values(5, 14)] int blocksPerPayload)
    {
        ManualTimeProvider time = new();
        using TransportMemoryPoolFactory factory = new(time);
        using TransportMemoryPool pool = (TransportMemoryPool)factory.Create();

        // An hour of slots, each payload held well under a millisecond; every tenth slot is missed.
        for (int slot = 0; slot < 300; slot++)
        {
            if (slot % 10 != 9) ReturnBlocks(RentBlocks(pool, blocksPerPayload));
            time.Advance(Slot);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.BlocksAllocated, Is.EqualTo(blocksPerPayload), "the blocks of one payload serve the next");
            Assert.That(pool.BlocksReleased, Is.Zero);
            Assert.That(pool.RetainedBlocks, Is.EqualTo(blocksPerPayload));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public void Concurrent_rents_and_returns_keep_blocks_exclusive_and_counts_consistent()
    {
        const int workers = 8;
        const int rounds = 5_000;
        using TransportMemoryPool pool = new();
        using CancellationTokenSource trimming = new();
        int clashes = 0;

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
                int count = random.Next(1, 7);
                for (int i = 0; i < count; i++)
                {
                    IMemoryOwner<byte> owner = pool.Rent();
                    MemoryMarshal.Write(owner.Memory.Span, in stamp);
                    MemoryMarshal.Write(owner.Memory.Span[^sizeof(long)..], in stamp);
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
            Assert.That(pool.BlocksInUse, Is.Zero);
            Assert.That(pool.BlocksAllocated - pool.BlocksReleased, Is.EqualTo(pool.RetainedBlocks), "every block is retained, released or in use");
            Assert.That(pool.BlocksAllocated, Is.LessThanOrEqualTo(workers * 6 + pool.BlocksReleased));
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
            Assert.That(pool.RetainedBlocks, Is.EqualTo(50));
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
            Assert.That(pool.RetainedBlocks, Is.Zero);
            Assert.That(pool.BlocksReleased, Is.Zero);
            Assert.That(() => pool.Rent(), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Startup_gives_kestrel_the_pool_and_large_bodies_cross_it_intact()
    {
        JsonRpcConfig rpcConfig = new();
        using GCKeeper gcKeeper = new(NoGCStrategy.Instance, LimboLogs.Instance);
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
            .ConfigureKestrel(static options => options.Listen(IPAddress.Loopback, 0))
            .ConfigureServices(services =>
            {
                services.AddSingleton(configProvider);
                new Startup().ConfigureServices(services);
            });
        WebApplication app = builder.Build();
        app.Run(static async ctx =>
        {
            using MemoryStream body = new();
            await ctx.Request.Body.CopyToAsync(body);
            ctx.Response.ContentLength = body.Length;
            await ctx.Response.Body.WriteAsync(body.GetBuffer().AsMemory(0, (int)body.Length));
        });
        await app.StartAsync();
        IMemoryPoolFactory<byte> factory = app.Services.GetRequiredService<IMemoryPoolFactory<byte>>();
        TransportMemoryPoolFactory ours = factory as TransportMemoryPoolFactory;
        bool trimmingWhileServing = ours?.IsTrimming ?? false;

        byte[] sent = new byte[5 * TransportMemoryPool.BlockSize + 123];
        new Random(42).NextBytes(sent);
        Uri address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using HttpClient client = new();
        using HttpResponseMessage response = await client.PostAsync(address, new ByteArrayContent(sent));
        byte[] received = await response.Content.ReadAsByteArrayAsync();
        await app.StopAsync();
        // Kestrel disposes its pools when it stops, and the last one stops the timer.
        bool trimmingAfterStop = ours?.IsTrimming ?? true;
        await app.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory, Is.InstanceOf<TransportMemoryPoolFactory>());
            Assert.That(ours?.PoolsCreated, Is.GreaterThan(0), "Kestrel did not take its pools from the factory");
            Assert.That(trimmingWhileServing, Is.True);
            Assert.That(trimmingAfterStop, Is.False, "the timer outlived Kestrel's pools");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(received, Is.EqualTo(sent));
        }
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
