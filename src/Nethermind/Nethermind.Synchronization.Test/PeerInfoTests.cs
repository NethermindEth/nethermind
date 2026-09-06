// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
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
                Assert.That(peer.IncreaseWeakness(contexts), Is.EqualTo(AllocationContexts.None));
            Assert.That(peer.IncreaseWeakness(contexts), Is.EqualTo(contexts));
        }

        [TestCaseSource(nameof(ContextCases))]
        public void Sleep_wakes_when_window_elapsed(AllocationContexts contexts)
        {
            PeerInfo peer = NewPeer();
            RaiseWeaknessUntilSleeping(peer, contexts);
            peer.PutToSleep(contexts, DateTime.MinValue);
            Assert.That(peer.IsAsleep(contexts), Is.True);

            peer.TryToWakeUp(DateTime.MinValue, TimeSpan.Zero);

            Assert.That(peer.IsAsleep(contexts), Is.False);
        }

        [TestCaseSource(nameof(ContextCases))]
        public void Sleep_stays_within_window(AllocationContexts contexts)
        {
            PeerInfo peer = NewPeer();
            RaiseWeaknessUntilSleeping(peer, contexts);
            peer.PutToSleep(contexts, DateTime.MinValue);

            peer.TryToWakeUp(DateTime.MinValue.AddSeconds(2), TimeSpan.FromSeconds(3));

            Assert.That(peer.IsAsleep(contexts), Is.True);
            Assert.That(peer.CanBeAllocated(contexts), Is.False);
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

            Assert.That(peer.IsAsleep(AllocationContexts.Bodies), Is.False);
            Assert.That(peer.IsAsleep(AllocationContexts.Receipts), Is.False);
        }

        [Test]
        public void WakeUp_clears_the_composite_Blocks_weakness_nibble()
        {
            // Regression: waking must reset the Blocks composite weakness nibble, not just its member bits,
            // so re-sleeping still takes SleepThreshold reports rather than one.
            DateTime t0 = DateTime.UtcNow;
            PeerInfo peer = NewPeer();

            RaiseWeaknessUntilSleeping(peer, AllocationContexts.Blocks);
            peer.PutToSleep(AllocationContexts.Blocks, t0);

            peer.TryToWakeUp(t0 + TimeSpan.FromHours(1), TimeSpan.Zero);
            Assert.That(peer.IsAsleep(AllocationContexts.Blocks), Is.False);

            // With the nibble reset, a single fresh weak report must not immediately re-sleep.
            Assert.That(peer.IncreaseWeakness(AllocationContexts.Blocks), Is.EqualTo(AllocationContexts.None));
        }

        [Test]
        public void Context_slot_mapping_round_trips_for_every_tracked_context()
        {
            (AllocationContexts Context, int Index)[] expected =
            [
                (AllocationContexts.Headers, 0),
                (AllocationContexts.Bodies, 1),
                (AllocationContexts.Receipts, 2),
                (AllocationContexts.State, 3),
                (AllocationContexts.Snap, 4),
                (AllocationContexts.ForwardHeader, 5),
                (AllocationContexts.BlockAccessLists, 6),
                (AllocationContexts.Blocks, 7),
            ];

            Assert.That(expected, Has.Length.EqualTo(WeaknessTracking.TrackedContextCount));
            foreach ((AllocationContexts context, int index) in expected)
            {
                Assert.That(WeaknessTracking.IndexOf(context), Is.EqualTo(index));
                Assert.That(WeaknessTracking.ContextAt(index), Is.EqualTo(context));
            }
        }

        [TestCaseSource(nameof(ContextCases))]
        public void TryAllocate_then_Free_round_trip(AllocationContexts contexts)
        {
            // Explicit single-slot allowances keep the round-trip mechanic tight: one alloc fills,
            // one free clears.
            PeerInfo peer = NewPeer(AllocationAllowances.Single);

            Assert.That(peer.IsAllocated(contexts), Is.False);
            Assert.That(peer.TryAllocate(contexts), Is.True);
            Assert.That(peer.IsAllocated(contexts), Is.True);
            Assert.That(peer.CanBeAllocated(contexts), Is.False);

            peer.Free(contexts);
            Assert.That(peer.IsAllocated(contexts), Is.False);
            Assert.That(peer.CanBeAllocated(contexts), Is.True);
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
                Assert.That(peer.IsAllocationFull(ctx), Is.False);
                Assert.That(peer.TryAllocate(ctx), Is.True);
            }
            Assert.That(peer.IsAllocationFull(ctx), Is.True);
            Assert.That(peer.CanBeAllocated(ctx), Is.False);

            peer.Free(ctx);
            Assert.That(peer.IsAllocationFull(ctx), Is.False);
            Assert.That(peer.TryAllocate(ctx), Is.True);
            Assert.That(peer.IsAllocationFull(ctx), Is.True);
        }

        [Test]
        public void Allocating_Blocks_takes_only_its_member_subcontexts()
        {
            // Blocks = Bodies | Receipts (Headers excluded), so allocating Blocks must leave Headers free
            // while marking Bodies and Receipts as taken; freeing one member re-opens only that slot.
            PeerInfo peer = NewPeer();
            Assert.That(peer.TryAllocate(AllocationContexts.Blocks), Is.True);

            Assert.That(peer.IsAllocated(AllocationContexts.Bodies), Is.True);
            Assert.That(peer.IsAllocated(AllocationContexts.Receipts), Is.True);
            Assert.That(peer.IsAllocated(AllocationContexts.Headers), Is.False);
            Assert.That(peer.CanBeAllocated(AllocationContexts.Headers), Is.True);

            peer.Free(AllocationContexts.Receipts);
            Assert.That(peer.IsAllocated(AllocationContexts.Receipts), Is.False);
            Assert.That(peer.IsAllocated(AllocationContexts.Bodies), Is.True);
        }

        [Test]
        public void Sleeping_a_composite_context_blocks_allocation_of_its_members()
        {
            PeerInfo peer = NewPeer();
            peer.PutToSleep(AllocationContexts.Blocks, DateTime.MinValue);
            Assert.That(peer.CanBeAllocated(AllocationContexts.Bodies), Is.False);
        }

        [Test]
        public void Free_clamps_at_allowance()
        {
            // Spurious frees (no prior TryAllocate) must not push the slot above its configured allowance.
            AllocationAllowances allowances = new(headers: 2, bodies: 1, receipts: 1, state: 1, snap: 1, forwardHeader: 1);
            PeerInfo peer = NewPeer(allowances);

            peer.Free(AllocationContexts.Headers);
            Assert.That(peer.AvailableAllocationSlots.Headers, Is.EqualTo(2), "free with no prior allocate must not exceed the cap");

            Assert.That(peer.TryAllocate(AllocationContexts.Headers), Is.True);
            Assert.That(peer.AvailableAllocationSlots.Headers, Is.EqualTo(1));
            peer.Free(AllocationContexts.Headers);
            Assert.That(peer.AvailableAllocationSlots.Headers, Is.EqualTo(2));
            peer.Free(AllocationContexts.Headers);
            Assert.That(peer.AvailableAllocationSlots.Headers, Is.EqualTo(2), "second free must clamp at the cap");
        }

        [Test]
        public void Free_of_unallocated_context_leaves_other_contexts_intact()
        {
            PeerInfo peer = NewPeer();
            Assert.That(peer.TryAllocate(AllocationContexts.Headers), Is.True);

            peer.Free(AllocationContexts.Bodies);

            Assert.That(peer.IsAllocated(AllocationContexts.Headers), Is.True);
            Assert.That(peer.IsAllocated(AllocationContexts.Bodies), Is.False);
        }

        [Test]
        public void TryAllocate_rolls_back_on_partial_failure()
        {
            // Take Bodies first; then Blocks (= Bodies | Receipts) must fail without leaking the Receipts slot.
            // Single-slot allowances make the rollback observable: Bodies sits at 0, Receipts must end at 1.
            PeerInfo peer = NewPeer(AllocationAllowances.Single);
            Assert.That(peer.TryAllocate(AllocationContexts.Bodies), Is.True);

            Assert.That(peer.TryAllocate(AllocationContexts.Blocks), Is.False);

            Assert.That(peer.AvailableAllocationSlots.Bodies, Is.EqualTo(0));
            Assert.That(peer.AvailableAllocationSlots.Receipts, Is.EqualTo(1), "rollback must restore the partially-claimed slot");
        }

        [Test]
        public void Concurrent_resleep_and_wake_never_leave_peer_permanently_asleep()
        {
            PeerInfo peer = NewPeer();
            DateTime sleptAt = DateTime.UtcNow;
            DateTime wakeAt = sleptAt + TimeSpan.FromSeconds(10);

            const int iterations = 25_000;
            using Barrier barrier = new(3);
            using CancellationTokenSource cts = new();

            void RunRounds(Action action)
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        barrier.SignalAndWait(cts.Token);
                        action();
                        barrier.SignalAndWait(cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }

            Thread sleeper = new(() => RunRounds(() => peer.PutToSleep(AllocationContexts.Receipts, sleptAt)));
            Thread waker = new(() => RunRounds(() => peer.TryToWakeUp(wakeAt, TimeSpan.Zero)));
            sleeper.Start();
            waker.Start();

            int stuckAt = -1;
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    peer.PutToSleep(AllocationContexts.Receipts, sleptAt);
                    barrier.SignalAndWait(cts.Token);
                    barrier.SignalAndWait(cts.Token);

                    peer.TryToWakeUp(wakeAt, TimeSpan.Zero);
                    if (peer.IsAsleep(AllocationContexts.Receipts))
                    {
                        stuckAt = i;
                        break;
                    }
                }
            }
            finally
            {
                cts.Cancel();
                sleeper.Join();
                waker.Join();
            }

            Assert.That(stuckAt, Is.EqualTo(-1), $"Peer left permanently asleep for Receipts at iteration {stuckAt}.");
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

            Assert.That(succeeded, Is.EqualTo(slots));
            Assert.That(peer.AvailableAllocationSlots.Headers, Is.EqualTo(0));
            Assert.That(peer.IsAllocationFull(AllocationContexts.Headers), Is.True);
        }

    }
}
