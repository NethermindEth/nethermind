// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.P2P;

public class DataColumnSidecarPoolBackfillFloorTests
{
    private const ulong Head = 13_410_304;

    private static (FaultyColumnsDb Db, DataColumnSidecarPool Pool) CreatePoolWithColumnAt(ulong slot)
    {
        FaultyColumnsDb db = new();
        DataColumnSidecarPool pool = new(store: new BeaconChainStore(db, BeaconChainSpec.Mainnet));
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, slot, Keccak.Compute("first")));
        return (db, pool);
    }

    [Test]
    public void The_servable_floor_follows_the_lowest_slot_the_backfill_completed_and_never_rises_with_a_later_call()
    {
        (_, DataColumnSidecarPool pool) = CreatePoolWithColumnAt(Head);

        pool.LowerCompletelyServableFloor(Head - 100);
        pool.LowerCompletelyServableFloor(Head - 40);

        Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(Head - 100));
    }

    [Test]
    public void A_column_the_store_refuses_below_the_lowered_floor_raises_the_floor_above_its_slot_again()
    {
        (FaultyColumnsDb db, DataColumnSidecarPool pool) = CreatePoolWithColumnAt(Head);
        pool.LowerCompletelyServableFloor(Head - 100);

        db.FailWrites = true;
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, Head - 50, Keccak.Compute("lost")));
        db.FailWrites = false;

        Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(Head - 49));
    }
    [Test]
    public void Pruning_clamps_backfill_to_retention_without_reapplying_the_startup_seed()
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        ManualTimestamper time = new(DateTimeOffset.FromUnixTimeSeconds((long)(spec.GenesisTime + Head * spec.SecondsPerSlot)).UtcDateTime);
        SlotClock clock = new(spec, time);
        BeaconChainStore store = new(new FaultyColumnsDb(), spec);
        DataColumnSidecarPool pool = new(store: store, clock: clock);
        pool.SeedCompletelyServableFloor(Head);
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, Head, Keccak.Compute("first")));
        pool.LowerCompletelyServableFloor(Head - 100);
        Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(Head - 100));

        ulong retained = DataAvailabilityBoundary.ComputeStartSlot(clock.CurrentEpoch, spec);
        pool.LowerCompletelyServableFloor(retained - 50);
        Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(retained));

        time.Add(TimeSpan.FromSeconds(spec.SlotsPerEpoch * spec.SecondsPerSlot));
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, clock.CurrentSlot, Keccak.Compute("later")));
        Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(retained + spec.SlotsPerEpoch));
    }

    [Test]
    public void Verified_repair_preserves_a_failure_after_verification_started([Values] bool laterFailure)
    {
        (FaultyColumnsDb db, DataColumnSidecarPool pool) = CreatePoolWithColumnAt(Head);
        pool.LowerCompletelyServableFloor(Head - 100);
        long before = pool.WriteFailureVersion;
        db.FailWrites = true;
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, Head - 50, Keccak.Compute("repair")));
        db.FailWrites = false;
        long verifiedVersion = laterFailure ? before : pool.WriteFailureVersion;
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, Head - 50, Keccak.Compute("repair")));
        pool.LowerCompletelyServableFloor(Head - 100, Head, verifiedVersion);
        Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(laterFailure ? Head - 49 : Head - 100));
    }

}
