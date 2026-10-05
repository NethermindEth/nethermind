// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Snappier;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public class ColumnGossipRouterReconstructionTests
{
    private const ulong CurrentSlot = 13_410_304;
    private const int Required = Eip7594DasConstants.RequiredColumnsForReconstruction;
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public void Sidecars_of_another_root_are_not_blocked_while_one_root_recovers()
    {
        using GatedRecovery recovery = new(CurrentSlot);
        (ColumnGossipRouter router, DataColumnSidecarPool pool, ConcurrentBag<DataColumnSidecar> received) = Create(recovery);
        Hash256 gatedRoot = Seed(pool, CurrentSlot);
        Hash256 otherRoot = Seed(pool, CurrentSlot - 1);

        Task gated = Task.Run(() => router.Handle(Required - 1, gloasTopic: false, Message(Required - 1, CurrentSlot)));
        bool entered = recovery.Entered.Wait(Timeout);
        Task<MessageValidity> other = Task.Run(() => router.Handle(Required - 1, gloasTopic: false, Message(Required - 1, CurrentSlot - 1)));
        bool otherFinishedWhileGated = other.Wait(Timeout);
        bool gatedStillRecovering = !gated.IsCompleted;
        recovery.Release.Set();
        bool gatedFinished = gated.Wait(Timeout);
        bool otherFinished = other.Wait(Timeout);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(entered, Is.True, "fixture: the first root's recovery started");
        Assert.That(otherFinishedWhileGated, Is.True, "the other root's sidecar is validated while the first root recovers");
        Assert.That(gatedStillRecovering, Is.True, "fixture: the first root was still recovering");
        Assert.That(gatedFinished, Is.True);
        Assert.That(otherFinished, Is.True);
        Assert.That(pool.TryGet(otherRoot, Eip7594DasConstants.NumberOfColumns - 1, out _), Is.True, "the other root was recovered too");
        Assert.That(pool.TryGet(gatedRoot, Eip7594DasConstants.NumberOfColumns - 1, out _), Is.True);
        Assert.That(received.Count, Is.EqualTo(2 * Eip7594DasConstants.NumberOfColumns - 2 * (Required - 1)), "every missing column of both roots was raised once");
    }

    [Test]
    public void A_root_is_never_recovered_twice_concurrently()
    {
        using GatedRecovery recovery = new(CurrentSlot);
        (ColumnGossipRouter router, DataColumnSidecarPool pool, ConcurrentBag<DataColumnSidecar> received) = Create(recovery);
        Hash256 root = Seed(pool, CurrentSlot);

        Task gated = Task.Run(() => router.Handle(Required - 1, gloasTopic: false, Message(Required - 1, CurrentSlot)));
        bool entered = recovery.Entered.Wait(Timeout);
        Task<MessageValidity> another = Task.Run(() => router.Handle(Required, gloasTopic: false, Message(Required, CurrentSlot)));
        bool anotherFinishedWhileGated = another.Wait(Timeout);
        int callsWhileGated = recovery.Calls;
        recovery.Release.Set();
        bool gatedFinished = gated.Wait(Timeout);
        bool anotherFinished = another.Wait(Timeout);
        typeof(ColumnGossipRouter).GetMethod("TrackHeldColumnAndMaybeReconstruct", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(router, [root, CurrentSlot]);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(entered, Is.True, "fixture: the recovery started");
        Assert.That(anotherFinishedWhileGated, Is.True, "a sidecar of the recovering root does not wait for it");
        Assert.That(callsWhileGated, Is.EqualTo(1), "the recovering root is not claimed again");
        Assert.That(gatedFinished, Is.True);
        Assert.That(anotherFinished, Is.True);
        Assert.That(recovery.Calls, Is.EqualTo(1), "a recovered root is never recovered again");
        Assert.That(pool.TryGet(root, Eip7594DasConstants.NumberOfColumns - 1, out _), Is.True);
        Assert.That(received.Select(static s => s.Index), Is.Unique);
    }

    [Test]
    public void A_reconstructed_column_callback_can_wait_for_another_roots_sidecar()
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(DataColumnReconstruction.TryReconstruct);
        Seed(pool, CurrentSlot);
        Seed(pool, CurrentSlot - 1);
        Task<MessageValidity>? other = null;
        bool finishedInCallback = false;
        router.DataColumnSidecarReceived += sidecar =>
        {
            if (sidecar.SignedBlockHeader!.Message!.Slot == CurrentSlot && sidecar.Index == Required)
            {
                other = Task.Run(() => router.Handle(Required - 1, gloasTopic: false, Message(Required - 1, CurrentSlot - 1)));
                finishedInCallback = other.Wait(Timeout);
            }
        };

        router.Handle(Required - 1, gloasTopic: false, Message(Required - 1, CurrentSlot));
        Assert.That(other, Is.Not.Null);
        Assert.That(other!.Wait(Timeout), Is.True);
        Assert.That(finishedInCallback, Is.True);
    }

    [Test]
    public void A_recovery_that_fails_or_throws_releases_its_root_for_the_next_column([Values] bool throws)
    {
        int calls = 0;
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create((IReadOnlyList<DataColumnSidecar> held, out DataColumnSidecar[] full) =>
        {
            full = [];
            if (Interlocked.Increment(ref calls) > 1)
            {
                return DataColumnReconstruction.TryReconstruct(held, out full);
            }

            return throws ? throw new InvalidOperationException("recovery failed") : false;
        });
        Hash256 root = Seed(pool, CurrentSlot);

        if (throws)
        {
            Assert.Throws<InvalidOperationException>(() => router.Handle(Required - 1, gloasTopic: false, Message(Required - 1, CurrentSlot)));
        }
        else
        {
            router.Handle(Required - 1, gloasTopic: false, Message(Required - 1, CurrentSlot));
        }

        bool recoveredEarly = pool.TryGet(root, Eip7594DasConstants.NumberOfColumns - 1, out _);
        router.Handle(Required, gloasTopic: false, Message(Required, CurrentSlot));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(recoveredEarly, Is.False, "fixture: the first recovery produced nothing");
        Assert.That(calls, Is.EqualTo(2), "the next held column retries the root");
        Assert.That(pool.TryGet(root, Eip7594DasConstants.NumberOfColumns - 1, out _), Is.True);
    }

    private static (ColumnGossipRouter Router, DataColumnSidecarPool Pool, ConcurrentBag<DataColumnSidecar> Received) Create(ColumnGossipRouter.DataColumnReconstructor reconstruct)
    {
        DataColumnSidecarPool pool = new();
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot + 6);
        ColumnGossipRouter router = new(Spec, new SlotClock(Spec, new ManualTimestamper(now)), LimboLogs.Instance, pool) { Reconstruct = reconstruct };
        router.Start(_ => new GossipDigestWindowTests.SilentTopic(), ForkDigest.Compute(Spec, 419_072), [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static i => (ulong)i)]);
        ConcurrentBag<DataColumnSidecar> received = [];
        router.DataColumnSidecarReceived += received.Add;
        return (router, pool, received);
    }

    private static (ColumnGossipRouter Router, DataColumnSidecarPool Pool, ConcurrentBag<DataColumnSidecar> Received) Create(GatedRecovery recovery) => Create(recovery.Recover);

    private static Hash256 Seed(DataColumnSidecarPool pool, ulong slot)
    {
        Hash256 root = SszRoots.HashTreeRoot(DataColumnSidecarTestFixture.BuildValidSidecar(0, slot).SignedBlockHeader!.Message!);
        for (ulong column = 0; column < Required - 1; column++)
        {
            pool.Add(root, slot, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot));
        }

        return root;
    }

    private static byte[] Message(ulong column, ulong slot) => Snappy.CompressToArray(DataColumnSidecar.Encode(DataColumnSidecarTestFixture.BuildValidSidecar(column, slot)));

    private sealed class GatedRecovery(ulong gatedSlot) : IDisposable
    {
        private int _calls;

        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public int Calls => Volatile.Read(ref _calls);

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }

        public bool Recover(IReadOnlyList<DataColumnSidecar> held, out DataColumnSidecar[] fullMatrix)
        {
            if (held[0].SignedBlockHeader!.Message!.Slot == gatedSlot)
            {
                Interlocked.Increment(ref _calls);
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException("The gated recovery was never released");
                }
            }

            return DataColumnReconstruction.TryReconstruct(held, out fullMatrix);
        }
    }
}
