// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.All)]
    public class PeerInfoTests
    {
        // The contexts that pass through TryAllocate/Free/IncreaseWeakness for parameterised tests.
        // Includes a mix of single-bit, composite (Blocks), and full (All) values to flush bit-position bugs.
        private static readonly AllocationContexts[] ContextCases =
        [
            AllocationContexts.Blocks,
            AllocationContexts.Receipts,
            AllocationContexts.Headers,
            AllocationContexts.Bodies,
            AllocationContexts.State,
            AllocationContexts.All,
        ];

        private static readonly AllocationContexts[] SingleBitContextCases =
        [
            AllocationContexts.Headers,
            AllocationContexts.Bodies,
            AllocationContexts.Receipts,
            AllocationContexts.State,
            AllocationContexts.Snap,
            AllocationContexts.ForwardHeader,
        ];

        private static PeerInfo NewPeer(AllocationAllowances? allowances = null) =>
            new(Substitute.For<ISyncPeer>(), allowances);

        /// <summary>
        /// Bump weakness up to (but not including) the sleep threshold and assert it has not slept,
        /// then bump once more and assert all <paramref name="contexts"/> have crossed the threshold.
        /// </summary>
        private static void RaiseWeaknessUntilSleeping(PeerInfo peer, AllocationContexts contexts)
        {
            for (int i = 0; i < PeerInfo.SleepThreshold - 1; i++)
                peer.IncreaseWeakness(contexts).Should().Be(AllocationContexts.None);
            peer.IncreaseWeakness(contexts).Should().Be(contexts);
        }

        // ── Sleep / wake lifecycle, parameterised over context combinations ─────────────────────

        [TestCaseSource(nameof(ContextCases))]
        public void Sleep_lifecycle_succeeds_when_wake_window_elapsed(AllocationContexts contexts)
        {
            PeerInfo peer = NewPeer();
            RaiseWeaknessUntilSleeping(peer, contexts);

            peer.PutToSleep(contexts, DateTime.MinValue);
            peer.IsAsleep(contexts).Should().BeTrue();

            peer.TryToWakeUp(DateTime.MinValue, TimeSpan.Zero);
            peer.IsAsleep(contexts).Should().BeFalse();
        }

        [TestCaseSource(nameof(ContextCases))]
        public void Sleep_lifecycle_stays_asleep_within_wake_window(AllocationContexts contexts)
        {
            PeerInfo peer = NewPeer();
            RaiseWeaknessUntilSleeping(peer, contexts);

            peer.PutToSleep(contexts, DateTime.MinValue);
            peer.TryToWakeUp(DateTime.MinValue.AddSeconds(2), TimeSpan.FromSeconds(3));

            peer.IsAsleep(contexts).Should().BeTrue();
            peer.CanBeAllocated(contexts).Should().BeFalse();
        }

        // ── Allocate / free, parameterised ──────────────────────────────────────────────────────

        [TestCaseSource(nameof(ContextCases))]
        public void TryAllocate_then_Free_round_trip(AllocationContexts contexts)
        {
            PeerInfo peer = NewPeer();

            peer.IsAllocated(contexts).Should().BeFalse();
            peer.TryAllocate(contexts).Should().BeTrue();
            peer.IsAllocated(contexts).Should().BeTrue();
            peer.CanBeAllocated(contexts).Should().BeFalse();

            peer.Free(contexts);
            peer.IsAllocated(contexts).Should().BeFalse();
            peer.CanBeAllocated(contexts).Should().BeTrue();
        }

        [TestCaseSource(nameof(SingleBitContextCases))]
        public void TryAllocate_drains_extra_slots_until_full(AllocationContexts ctx)
        {
            const byte slots = 5;
            AllocationAllowances allowances = AllocationAllowances.Default;
            allowances[ctx] = slots;
            PeerInfo peer = NewPeer(allowances);

            for (int i = 0; i < slots; i++)
            {
                peer.IsAllocationFull(ctx).Should().BeFalse();
                peer.TryAllocate(ctx).Should().BeTrue();
            }
            peer.IsAllocationFull(ctx).Should().BeTrue();
            peer.CanBeAllocated(ctx).Should().BeFalse();

            // One free re-opens exactly one slot.
            peer.Free(ctx);
            peer.IsAllocationFull(ctx).Should().BeFalse();
            peer.TryAllocate(ctx).Should().BeTrue();
            peer.IsAllocationFull(ctx).Should().BeTrue();
        }

        // ── Composite-context behaviour ─────────────────────────────────────────────────────────

        [Test]
        public void Allocating_Blocks_takes_only_its_member_subcontexts()
        {
            // Master's Blocks = Bodies | Receipts (Headers is excluded), so allocating Blocks must leave
            // Headers free while marking Bodies and Receipts as taken.
            PeerInfo peer = NewPeer();
            peer.TryAllocate(AllocationContexts.Blocks).Should().BeTrue();

            peer.IsAllocated(AllocationContexts.Bodies).Should().BeTrue();
            peer.IsAllocated(AllocationContexts.Receipts).Should().BeTrue();
            peer.IsAllocated(AllocationContexts.Headers).Should().BeFalse();

            peer.CanBeAllocated(AllocationContexts.Bodies).Should().BeFalse();
            peer.CanBeAllocated(AllocationContexts.Receipts).Should().BeFalse();
            peer.CanBeAllocated(AllocationContexts.Headers).Should().BeTrue();

            peer.Free(AllocationContexts.Receipts);
            peer.IsAllocated(AllocationContexts.Receipts).Should().BeFalse();
            peer.IsAllocated(AllocationContexts.Bodies).Should().BeTrue();
        }

        [Test]
        public void Sleeping_a_composite_context_blocks_allocation_of_its_members()
        {
            PeerInfo peer = NewPeer();
            peer.PutToSleep(AllocationContexts.Blocks, DateTime.MinValue);
            peer.CanBeAllocated(AllocationContexts.Bodies).Should().BeFalse();
        }

        [Test]
        public void WakeUp_only_clears_contexts_past_their_individual_sleep_window()
        {
            // Bodies was put to sleep 10s ago and Blocks just now. With a 5s window only Bodies should wake first;
            // 10s later Blocks (and the rest of its sub-contexts) wakes too.
            DateTime t0 = DateTime.UtcNow;
            PeerInfo peer = NewPeer();

            peer.PutToSleep(AllocationContexts.Blocks, t0);
            peer.PutToSleep(AllocationContexts.Bodies, t0 - TimeSpan.FromSeconds(10));

            peer.TryToWakeUp(t0, TimeSpan.FromSeconds(5));
            peer.TryToWakeUp(t0 + TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

            peer.IsAsleep(AllocationContexts.Bodies).Should().BeFalse();
            peer.IsAsleep(AllocationContexts.Receipts).Should().BeFalse();
        }

        // ── Multi-slot edge cases ───────────────────────────────────────────────────────────────

        [Test]
        public void Free_does_not_exceed_allowance()
        {
            // Spurious frees (no prior TryAllocate) must not push the slot above its configured allowance.
            AllocationAllowances allowances = new(headers: 2, bodies: 1, receipts: 1, state: 1, snap: 1, forwardHeader: 1);
            PeerInfo peer = NewPeer(allowances);

            peer.AvailableAllocationSlots.Headers.Should().Be(2);
            peer.Free(AllocationContexts.Headers);
            peer.AvailableAllocationSlots.Headers.Should().Be(2);

            peer.TryAllocate(AllocationContexts.Headers).Should().BeTrue();
            peer.AvailableAllocationSlots.Headers.Should().Be(1);
            peer.Free(AllocationContexts.Headers);
            peer.AvailableAllocationSlots.Headers.Should().Be(2);
            peer.Free(AllocationContexts.Headers);
            peer.AvailableAllocationSlots.Headers.Should().Be(2, "Free should clamp at the configured allowance");
        }

        [Test]
        public void Free_of_unallocated_context_leaves_other_contexts_intact()
        {
            // TryAllocate(Headers) should not be undone by Free(Bodies).
            PeerInfo peer = NewPeer();
            peer.TryAllocate(AllocationContexts.Headers).Should().BeTrue();

            peer.Free(AllocationContexts.Bodies);

            peer.IsAllocated(AllocationContexts.Headers).Should().BeTrue();
            peer.IsAllocated(AllocationContexts.Bodies).Should().BeFalse();
        }

        [Test]
        public void TryAllocate_rolls_back_on_partial_failure()
        {
            // Take Bodies first; then Blocks (= Bodies | Receipts) must fail without leaking the Receipts slot.
            PeerInfo peer = NewPeer();
            peer.TryAllocate(AllocationContexts.Bodies).Should().BeTrue();

            peer.TryAllocate(AllocationContexts.Blocks).Should().BeFalse();

            peer.AvailableAllocationSlots.Bodies.Should().Be(0);
            peer.AvailableAllocationSlots.Receipts.Should().Be(1, "rollback must restore the partially-claimed slot");
            peer.IsAllocationFull(AllocationContexts.Receipts).Should().BeFalse();
        }

        [Test]
        public void AllocationAllowances_is_packed_ulong_size() =>
            System.Runtime.InteropServices.Marshal.SizeOf<AllocationAllowances>().Should().Be(sizeof(ulong));

        [Test]
        public void AllocationAllowances_default_static_has_all_ones()
        {
            // Sanity: Default must initialize Packed via the primary ctor's field initializer.
            AllocationAllowances d = AllocationAllowances.Default;
            d.Headers.Should().Be(1);
            d.Bodies.Should().Be(1);
            d.Receipts.Should().Be(1);
            d.State.Should().Be(1);
            d.Snap.Should().Be(1);
            d.ForwardHeader.Should().Be(1);
        }

        [Test]
        public void AllocationAllowances_packs_and_unpacks_correctly()
        {
            AllocationAllowances a = new(headers: 1, bodies: 2, receipts: 3, state: 4, snap: 5, forwardHeader: 6);
            a.Headers.Should().Be(1);
            a.Bodies.Should().Be(2);
            a.Receipts.Should().Be(3);
            a.State.Should().Be(4);
            a.Snap.Should().Be(5);
            a.ForwardHeader.Should().Be(6);
            a[AllocationContexts.Headers].Should().Be(1);
            a[AllocationContexts.Bodies].Should().Be(2);
            a[AllocationContexts.Receipts].Should().Be(3);
            a[AllocationContexts.State].Should().Be(4);
            a[AllocationContexts.Snap].Should().Be(5);
            a[AllocationContexts.ForwardHeader].Should().Be(6);
        }

        [Test]
        public void Default_peer_can_allocate_state()
        {
            // Mirrors the failing StateSyncDispatcherTests scenario: a fresh peer with 1-slot allowance
            // must accept State allocation.
            PeerInfo peer = NewPeer();
            peer.CanBeAllocated(AllocationContexts.State).Should().BeTrue();
            peer.TryAllocate(AllocationContexts.State).Should().BeTrue();
            peer.IsAllocationFull(AllocationContexts.State).Should().BeTrue();
        }

        [Test]
        public void Concurrent_TryAllocate_does_not_oversubscribe()
        {
            // Many threads racing on a fixed slot count: total successful allocations must equal the allowance,
            // verifying the lock-free CAS loop in TryAllocate neither loses nor duplicates slots under contention.
            const byte slots = 8;
            const int threads = 32;
            AllocationAllowances allowances = new(headers: slots, bodies: 1, receipts: 1, state: 1, snap: 1, forwardHeader: 1);
            PeerInfo peer = NewPeer(allowances);
            int succeeded = 0;
            using Barrier barrier = new(threads);

            Task[] tasks = new Task[threads];
            for (int t = 0; t < threads; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    if (peer.TryAllocate(AllocationContexts.Headers))
                        Interlocked.Increment(ref succeeded);
                });
            }
            Task.WaitAll(tasks);

            succeeded.Should().Be(slots);
            peer.AvailableAllocationSlots.Headers.Should().Be(0);
            peer.IsAllocationFull(AllocationContexts.Headers).Should().BeTrue();
        }
    }
}
