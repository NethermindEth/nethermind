// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class CodePrefetcherTests
{
    private static readonly byte[] Code = [0x60, 0x01, 0x00];
    private static readonly ValueHash256 CodeHash = ValueKeccak.Compute(Code);
    private static readonly ValueHash256 OtherHash = ValueKeccak.Compute([0x01]);

    [Test]
    public void Execution_reaching_code_still_being_read_waits_for_that_read_instead_of_reading_it_again()
    {
        GatedCodeDb codeDb = new();
        CodePrefetcher prefetcher = new(codeDb);

        Task prefetching = Task.Run(() => prefetcher.Prefetch(in CodeHash));
        Assert.That(codeDb.ReadStarted.Wait(TimeSpan.FromSeconds(10)), "the prefetch never started its read");

        Task<ReadOnlyMemory<byte>> taking = Task.Run(() => prefetcher.Take(in CodeHash));
        Assert.That(taking.Wait(TimeSpan.FromMilliseconds(100)), Is.False, "execution must wait for the read in flight");

        codeDb.Release();
        Assert.That(taking.Wait(TimeSpan.FromSeconds(10)), "execution never received the prefetched code");
        prefetching.Wait(TimeSpan.FromSeconds(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(taking.Result, Is.SequenceEqualTo(Code));
            Assert.That(codeDb.Reads, Is.EqualTo(1));
        }
    }

    [Test]
    public void Taken_code_is_not_kept_so_a_block_of_large_contracts_is_not_held_until_the_block_ends()
    {
        GatedCodeDb codeDb = GatedCodeDb.Open(freshBuffers: true);
        CodePrefetcher prefetcher = new(codeDb);

        WeakReference taken = PrefetchAndTake(prefetcher);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.That(taken.IsAlive, Is.False, "the prefetcher must not hold code it handed out");
        Assert.That(prefetcher.Take(in CodeHash).IsNull(), "a second taker reads the code itself");
        GC.KeepAlive(prefetcher);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PrefetchAndTake(CodePrefetcher prefetcher)
    {
        prefetcher.Prefetch(in CodeHash);
        ReadOnlyMemory<byte> code = prefetcher.Take(in CodeHash);
        Assert.That(MemoryMarshal.TryGetArray(code, out ArraySegment<byte> array), "the test store serves arrays");
        Assert.That(code, Is.SequenceEqualTo(Code));
        return new WeakReference(array.Array);
    }

    [Test]
    public void Code_execution_already_took_is_not_read_again_by_a_later_prefetch()
    {
        GatedCodeDb codeDb = GatedCodeDb.Open();
        CodePrefetcher prefetcher = new(codeDb);

        Assert.That(prefetcher.Take(in CodeHash).IsNull(), "nothing was prefetched");
        prefetcher.Prefetch(in CodeHash);

        Assert.That(codeDb.Reads, Is.Zero);
    }

    [Test]
    public void Code_the_code_cache_holds_is_not_read_ahead_as_execution_needs_no_read_for_it()
    {
        GatedCodeDb codeDb = GatedCodeDb.Open();
        StaticCodeCache codeCache = new(16);
        codeCache.Set(in CodeHash, new CodeInfo(Code));
        CodePrefetcher prefetcher = new(codeDb, codeCache);

        prefetcher.Prefetch(in CodeHash);

        Assert.That(codeDb.Reads, Is.Zero);
    }

    [Test]
    public void Prefetched_code_no_one_takes_is_capped_as_an_access_list_can_name_contracts_the_block_never_runs()
    {
        GatedCodeDb codeDb = GatedCodeDb.Open();
        CodePrefetcher prefetcher = new(codeDb, maxHeldBytes: Code.Length);

        prefetcher.Prefetch(in CodeHash);
        prefetcher.Prefetch(in OtherHash);
        Assert.That(codeDb.Reads, Is.EqualTo(1), "the cap is reached, so the second contract is left to execution");

        prefetcher.Take(in CodeHash);
        prefetcher.Prefetch(in OtherHash);
        Assert.That(codeDb.Reads, Is.EqualTo(2), "taking the first contract frees its share of the cap");
    }

    [Test]
    public void Queued_code_is_read_by_a_background_reader_and_taken_without_a_second_read()
    {
        GatedCodeDb codeDb = GatedCodeDb.Open();
        CodePrefetcher prefetcher = new(codeDb);

        prefetcher.Enqueue(in CodeHash);

        Assert.That(codeDb.ReadStarted.Wait(TimeSpan.FromSeconds(10)), "no reader picked up the queued code");
        Assert.That(prefetcher.Take(in CodeHash), Is.SequenceEqualTo(Code));
        Assert.That(codeDb.Reads, Is.EqualTo(1));
    }

    [Test]
    public void Code_queued_while_the_readers_sleep_is_still_read()
    {
        GatedCodeDb codeDb = GatedCodeDb.Open();
        CodePrefetcher prefetcher = new(codeDb);

        for (int i = 0; i < 20; i++)
        {
            // Every reader is asleep before the code is queued, so only a wake-up can get it read.
            if (i > 0)
            {
                Assert.That(SpinWait.SpinUntil(() => CodePrefetcher.IdleReaders == CodePrefetcher.Readers, TimeSpan.FromSeconds(10)),
                    "the readers never all went to sleep");
            }

            prefetcher.Enqueue(ValueKeccak.Compute(BitConverter.GetBytes(i)));
            Assert.That(SpinWait.SpinUntil(() => codeDb.Reads == i + 1, TimeSpan.FromSeconds(5)), $"code queued at {i} was never read");
        }
    }

    [Test]
    public void A_stopped_prefetcher_reads_nothing_more_once_its_block_is_done()
    {
        GatedCodeDb codeDb = GatedCodeDb.Open();
        CodePrefetcher prefetcher = new(codeDb);

        prefetcher.Stop();
        prefetcher.Enqueue(in CodeHash);
        prefetcher.Prefetch(in OtherHash);

        // Readers are idle, so a queued read would start at once.
        Assert.That(codeDb.ReadStarted.Wait(TimeSpan.FromMilliseconds(500)), Is.False);
    }

    [Test]
    public void Stopping_does_not_wait_on_a_slow_read_and_does_not_hold_what_it_returns()
    {
        GatedCodeDb codeDb = new(freshBuffers: true);
        CodePrefetcher prefetcher = new(codeDb);

        prefetcher.Enqueue(in CodeHash);
        Assert.That(codeDb.ReadStarted.Wait(TimeSpan.FromSeconds(10)), "no reader picked up the queued code");

        // The block ends on the commit path, so it must not wait for the store.
        Assert.That(Task.Run(prefetcher.Stop).Wait(TimeSpan.FromSeconds(10)), "stop waited for the read in progress");

        codeDb.Release();
        Assert.That(SpinWait.SpinUntil(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return codeDb.LastServed is { IsAlive: false };
        }, TimeSpan.FromSeconds(10)), "code read after the stop stayed held");
        Assert.That(prefetcher.Take(in CodeHash).IsNull(), "execution reads the code itself");
        GC.KeepAlive(prefetcher);
    }

    [Test]
    public void A_read_added_after_a_stop_cleared_the_reads_is_not_held()
    {
        GatedCodeDb codeDb = GatedCodeDb.Open();
        StoppingCodeCache codeCache = new();
        CodePrefetcher prefetcher = new(codeDb, codeCache);
        codeCache.Prefetcher = prefetcher;

        // The cache lookup comes after the stop check and before the read is added, so the stop lands between them.
        prefetcher.Prefetch(in CodeHash);

        Assert.That(codeDb.Reads, Is.EqualTo(1), "the read raced the stop, as a background reader can");
        Assert.That(prefetcher.Take(in CodeHash).IsNull(), "a stopped prefetcher must not hold the code it read");
    }

    /// <summary>Misses every lookup, stopping the prefetcher as it looks.</summary>
    private sealed class StoppingCodeCache : ICodeCache
    {
        public CodePrefetcher? Prefetcher { get; set; }

        public CodeInfo? Get(in ValueHash256 codeHash)
        {
            Prefetcher?.Stop();
            return null;
        }

        public void Set(in ValueHash256 codeHash, CodeInfo codeInfo) { }

        public void Clear() { }
    }

    [Test]
    public void Stopping_drops_code_no_one_took_so_an_idle_reader_does_not_hold_a_finished_block()
    {
        GatedCodeDb codeDb = GatedCodeDb.Open(freshBuffers: true);
        CodePrefetcher prefetcher = new(codeDb);

        prefetcher.Prefetch(in CodeHash);
        prefetcher.Stop();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.That(codeDb.LastServed!.IsAlive, Is.False, "a stopped prefetcher must not hold code");
        Assert.That(prefetcher.Take(in CodeHash).IsNull(), "execution reads the code itself");
        GC.KeepAlive(prefetcher);
    }

    [Test]
    public void A_failed_prefetch_leaves_the_read_and_its_error_to_execution()
    {
        CodePrefetcher prefetcher = new(GatedCodeDb.Open(fail: true));

        prefetcher.Prefetch(in CodeHash);

        Assert.That(prefetcher.Take(in CodeHash).IsNull());
    }

    /// <summary>Serves <see cref="Code"/>, or a copy of it, for every hash once released; counts and optionally fails reads.</summary>
    private sealed class GatedCodeDb(bool fail = false, bool freshBuffers = false) : IWorldStateScopeProvider.ICodeDb
    {
        private readonly ManualResetEventSlim _gate = new();
        private int _reads;

        public static GatedCodeDb Open(bool fail = false, bool freshBuffers = false)
        {
            GatedCodeDb codeDb = new(fail, freshBuffers);
            codeDb.Release();
            return codeDb;
        }

        public ManualResetEventSlim ReadStarted { get; } = new();
        public int Reads => Volatile.Read(ref _reads);

        /// <summary>The last fresh buffer served, without keeping it alive.</summary>
        public WeakReference? LastServed { get; private set; }

        public void Release() => _gate.Set();

        public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
        {
            Interlocked.Increment(ref _reads);
            ReadStarted.Set();
            _gate.Wait();
            if (fail) throw new InvalidOperationException("read failed");
            if (!freshBuffers) return Code;

            byte[] code = (byte[])Code.Clone();
            LastServed = new WeakReference(code);
            return code;
        }

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => throw new NotSupportedException();
    }
}
