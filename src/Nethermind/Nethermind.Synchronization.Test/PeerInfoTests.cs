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
        // Mix of single-bit, composite (Blocks), and full (All) values to flush bit-position bugs.
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
        /// Bumps weakness up to (but not including) the threshold, then once more so all
        /// <paramref name="contexts"/> become sleep candidates.
        /// </summary>
        private static void RaiseWeaknessUntilSleeping(PeerInfo peer, AllocationContexts contexts)
        {
            for (int i = 0; i < PeerInfo.SleepThreshold - 1; i++)
                peer.IncreaseWeakness(contexts).Should().Be(AllocationContexts.None);
            peer.IncreaseWeakness(contexts).Should().Be(contexts);
        }

        [TestCaseSource(nameof(ContextCases))]
        public void Sleep_wakes_when_window_elapsed(AllocationContexts contexts)
        {
            PeerInfo peer = NewPeer();
            RaiseWeaknessUntilSleeping(peer, contexts);
            peer.PutToSleep(contexts, DateTime.MinValue);
            peer.IsAsleep(contexts).Should().BeTrue();

            peer.TryToWakeUp(DateTime.MinValue, TimeSpan.Zero);

            peer.IsAsleep(contexts).Should().BeFalse();
        }

        [TestCaseSource(nameof(ContextCases))]
        public void Sleep_stays_within_window(AllocationContexts contexts)
        {
            PeerInfo peer = NewPeer();
            RaiseWeaknessUntilSleeping(peer, contexts);
            peer.PutToSleep(contexts, DateTime.MinValue);

            peer.TryToWakeUp(DateTime.MinValue.AddSeconds(2), TimeSpan.FromSeconds(3));

            peer.IsAsleep(contexts).Should().BeTrue();
            peer.CanBeAllocated(contexts).Should().BeFalse();
        }

        [Test]
        public void WakeUp_only_clears_contexts_past_their_individual_sleep_window()
        {
            // Bodies put to sleep 10s ago and Blocks just now. With a 5s window, only Bodies wakes first;
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

        [TestCaseSource(nameof(ContextCases))]
        public void TryAllocate_then_Free_round_trip(AllocationContexts contexts)
        {
            // Explicit single-slot allowances keep the round-trip mechanic tight: one alloc fills,
            // one free clears.
            PeerInfo peer = NewPeer(AllocationAllowances.Single);

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

            peer.Free(ctx);
            peer.IsAllocationFull(ctx).Should().BeFalse();
            peer.TryAllocate(ctx).Should().BeTrue();
            peer.IsAllocationFull(ctx).Should().BeTrue();
        }

        [Test]
        public void Allocating_Blocks_takes_only_its_member_subcontexts()
        {
            // Blocks = Bodies | Receipts (Headers excluded), so allocating Blocks must leave Headers free
            // while marking Bodies and Receipts as taken; freeing one member re-opens only that slot.
            PeerInfo peer = NewPeer();
            peer.TryAllocate(AllocationContexts.Blocks).Should().BeTrue();

            peer.IsAllocated(AllocationContexts.Bodies).Should().BeTrue();
            peer.IsAllocated(AllocationContexts.Receipts).Should().BeTrue();
            peer.IsAllocated(AllocationContexts.Headers).Should().BeFalse();
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
        public void Free_clamps_at_allowance()
        {
            // Spurious frees (no prior TryAllocate) must not push the slot above its configured allowance.
            AllocationAllowances allowances = new(headers: 2, bodies: 1, receipts: 1, state: 1, snap: 1, forwardHeader: 1);
            PeerInfo peer = NewPeer(allowances);

            peer.Free(AllocationContexts.Headers);
            peer.AvailableAllocationSlots.Headers.Should().Be(2, "free with no prior allocate must not exceed the cap");

            peer.TryAllocate(AllocationContexts.Headers).Should().BeTrue();
            peer.AvailableAllocationSlots.Headers.Should().Be(1);
            peer.Free(AllocationContexts.Headers);
            peer.AvailableAllocationSlots.Headers.Should().Be(2);
            peer.Free(AllocationContexts.Headers);
            peer.AvailableAllocationSlots.Headers.Should().Be(2, "second free must clamp at the cap");
        }

        [Test]
        public void Free_of_unallocated_context_leaves_other_contexts_intact()
        {
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
            // Single-slot allowances make the rollback observable: Bodies sits at 0, Receipts must end at 1.
            PeerInfo peer = NewPeer(AllocationAllowances.Single);
            peer.TryAllocate(AllocationContexts.Bodies).Should().BeTrue();

            peer.TryAllocate(AllocationContexts.Blocks).Should().BeFalse();

            peer.AvailableAllocationSlots.Bodies.Should().Be(0);
            peer.AvailableAllocationSlots.Receipts.Should().Be(1, "rollback must restore the partially-claimed slot");
        }

        [Test]
        public void Concurrent_TryAllocate_does_not_oversubscribe()
        {
            // Many threads racing on a fixed slot count: total successful allocations must equal the allowance,
            // verifying the lock-free CAS loop in TryAllocate neither loses nor duplicates slots.
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
