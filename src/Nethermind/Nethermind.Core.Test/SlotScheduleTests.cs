// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[TestFixture]
public class SlotScheduleTests
{
    private const ulong Genesis = 100;
    private const ulong Fork = Genesis + 5 * 12; // slot 5

    private static readonly SlotSchedule Scheduled = new(12, Genesis, [(Fork, 10_000)]);
    private static readonly SlotSchedule Unscheduled = new(12, Genesis);
    private static readonly SlotSchedule NoGenesis = new(12, null, [(Fork, 10_000)]);

    // 12 s from genesis, then 10 s from slot 5 (160), 6 s from slot 9 (200), and 8 s from 225, which is off the 6 s grid
    // and so starts at slot 14 (230).
    private static readonly (ulong, ulong)[] ThreeChanges = [(160, 10_000), (200, 6_000), (225, 8_000)];
    private static readonly SlotSchedule ThreeChangesGrid = new(12, Genesis, ThreeChanges);
    private static readonly SlotSchedule ThreeChangesNoGenesis = new(12, null, ThreeChanges);

    [TestCase(100UL, 0UL)]
    [TestCase(159UL, 4UL)]
    [TestCase(160UL, 5UL)]
    [TestCase(169UL, 5UL)]
    [TestCase(170UL, 6UL)]
    [TestCase(1_160UL, 105UL)]
    public void Slot_at_time_switches_length_at_the_fork(ulong timestamp, ulong slot)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Scheduled.TryGetSlot(timestamp, out ulong actual), Is.True);
            Assert.That(actual, Is.EqualTo(slot));
            Assert.That(Scheduled.GetSlotTimestamp(slot), Is.LessThanOrEqualTo(timestamp));
        }
    }

    [TestCase(4UL, 148UL)]
    [TestCase(5UL, 160UL)]
    [TestCase(6UL, 170UL)]
    public void Time_at_slot_switches_length_at_the_fork(ulong slot, ulong timestamp) =>
        Assert.That(Scheduled.GetSlotTimestamp(slot), Is.EqualTo(timestamp));

    [Test]
    public void Unscheduled_fork_keeps_the_configured_length()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Unscheduled.TryGetSlot(170, out ulong slot), Is.True);
            Assert.That(slot, Is.EqualTo(5UL));
            Assert.That(Unscheduled.GetSlotTimestamp(6), Is.EqualTo(172UL));
            Assert.That(Unscheduled.GetSecondsPerSlot(ulong.MaxValue), Is.EqualTo(12UL));
            Assert.That(Unscheduled.AddSlots(148, 3), Is.EqualTo(184UL));
            Assert.That(Unscheduled.AddSlotsUntil(148, 175), Is.EqualTo(184UL));
        }
    }

    [Test]
    public void Fork_off_the_grid_starts_at_the_next_boundary()
    {
        SlotSchedule schedule = new(12, Genesis, [(Fork + 5, 10_000)]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.TryGetSlot(171, out ulong before) && before == 5, Is.True);
            Assert.That(schedule.TryGetSlot(172, out ulong first) && first == 6, Is.True);
            Assert.That(schedule.TryGetSlot(182, out ulong second) && second == 7, Is.True);
            Assert.That(schedule.GetSecondsPerSlot(171), Is.EqualTo(12UL));
            Assert.That(schedule.GetSecondsPerSlot(172), Is.EqualTo(10UL));
        }
    }

    [Test]
    public void Fork_before_genesis_applies_from_genesis()
    {
        SlotSchedule schedule = new(12, Genesis, [(Genesis - 50, 10_000)]);
        Assert.That(schedule.TryGetSlot(Genesis + 25, out ulong slot) && slot == 2, Is.True);
    }

    [TestCase(159UL, 12UL)]
    [TestCase(160UL, 10UL)]
    public void Seconds_per_slot_switches_at_the_fork(ulong timestamp, ulong expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Scheduled.GetSecondsPerSlot(timestamp), Is.EqualTo(expected));
            Assert.That(NoGenesis.GetSecondsPerSlot(timestamp), Is.EqualTo(expected));
        }
    }

    [TestCase(148UL, 1UL, 160UL)]
    [TestCase(148UL, 2UL, 170UL)]
    [TestCase(136UL, 3UL, 170UL)]
    [TestCase(160UL, 2UL, 180UL)]
    [TestCase(150UL, 1UL, 162UL)]
    [TestCase(150UL, 2UL, 172UL)]
    public void Adding_slots_steps_by_the_length_each_step_starts_in(ulong timestamp, ulong slots, ulong expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Scheduled.AddSlots(timestamp, slots), Is.EqualTo(expected));
            Assert.That(NoGenesis.AddSlots(timestamp, slots), Is.EqualTo(expected));
        }
    }

    [TestCase(148UL, 0UL, 160UL)]
    [TestCase(148UL, 160UL, 160UL)]
    [TestCase(148UL, 161UL, 170UL)]
    [TestCase(100UL, 150UL, 160UL)]
    [TestCase(100UL, 165UL, 170UL)]
    [TestCase(160UL, 185UL, 190UL)]
    public void Adding_slots_until_a_target_takes_at_least_one_step(ulong timestamp, ulong target, ulong expected) =>
        Assert.That(Scheduled.AddSlotsUntil(timestamp, target), Is.EqualTo(expected));

    [TestCase(148UL, true)]
    [TestCase(149UL, false)]
    [TestCase(160UL, true)]
    [TestCase(165UL, false)]
    [TestCase(170UL, true)]
    [TestCase(172UL, false)]
    public void Slot_starts_follow_the_piecewise_grid(ulong timestamp, bool expected) =>
        Assert.That(Scheduled.IsSlotStart(timestamp), Is.EqualTo(expected));

    [Test]
    public void Without_genesis_slot_numbers_are_unavailable()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(NoGenesis.TryGetSlot(Fork, out _), Is.False);
            Assert.That(NoGenesis.GetSlotTimestamp(1), Is.Null);
            Assert.That(NoGenesis.IsSlotStart(Fork), Is.False);
        }
    }

    [TestCase(148UL, 4UL)]
    [TestCase(159UL, 4UL)]
    [TestCase(160UL, 5UL)]
    [TestCase(199UL, 8UL)]
    [TestCase(200UL, 9UL)]
    [TestCase(205UL, 9UL)]
    [TestCase(206UL, 10UL)]
    [TestCase(229UL, 13UL)]
    [TestCase(230UL, 14UL)]
    [TestCase(237UL, 14UL)]
    [TestCase(238UL, 15UL)]
    public void Slot_at_time_crosses_every_boundary(ulong timestamp, ulong slot)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ThreeChangesGrid.TryGetSlot(timestamp, out ulong actual), Is.True);
            Assert.That(actual, Is.EqualTo(slot));
            Assert.That(ThreeChangesGrid.GetSlotTimestamp(slot), Is.LessThanOrEqualTo(timestamp));
            Assert.That(ThreeChangesGrid.GetSlotTimestamp(slot + 1), Is.GreaterThan(timestamp));
        }
    }

    [TestCase(4UL, 148UL)]
    [TestCase(5UL, 160UL)]
    [TestCase(9UL, 200UL)]
    [TestCase(13UL, 224UL)]
    [TestCase(14UL, 230UL)]
    [TestCase(16UL, 246UL)]
    public void Time_at_slot_crosses_every_boundary(ulong slot, ulong timestamp) =>
        Assert.That(ThreeChangesGrid.GetSlotTimestamp(slot), Is.EqualTo(timestamp));

    [TestCase(159UL, 12UL, 12UL)]
    [TestCase(160UL, 10UL, 10UL)]
    [TestCase(199UL, 10UL, 10UL)]
    [TestCase(200UL, 6UL, 6UL)]
    [TestCase(225UL, 6UL, 8UL)]
    [TestCase(229UL, 6UL, 8UL)]
    [TestCase(230UL, 8UL, 8UL)]
    public void Seconds_per_slot_follows_each_entry(ulong timestamp, ulong withGenesis, ulong withoutGenesis)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ThreeChangesGrid.GetSecondsPerSlot(timestamp), Is.EqualTo(withGenesis));
            Assert.That(ThreeChangesNoGenesis.GetSecondsPerSlot(timestamp), Is.EqualTo(withoutGenesis));
        }
    }

    [TestCase(148UL, 1UL, 160UL)]
    [TestCase(148UL, 5UL, 200UL)]
    [TestCase(148UL, 6UL, 206UL)]
    [TestCase(148UL, 10UL, 230UL)]
    [TestCase(148UL, 11UL, 238UL)]
    [TestCase(150UL, 1UL, 162UL)]
    [TestCase(150UL, 2UL, 172UL)]
    [TestCase(150UL, 6UL, 208UL)]
    public void Adding_slots_crosses_every_boundary(ulong timestamp, ulong slots, ulong expected) =>
        Assert.That(ThreeChangesGrid.AddSlots(timestamp, slots), Is.EqualTo(expected));

    [Test]
    public void Without_genesis_each_entry_starts_at_its_timestamp()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ThreeChangesNoGenesis.AddSlots(148, 10), Is.EqualTo(230UL));
            Assert.That(ThreeChangesNoGenesis.AddSlots(148, 11), Is.EqualTo(238UL));
            Assert.That(ThreeChangesNoGenesis.AddSlots(200, 5), Is.EqualTo(230UL));
            Assert.That(ThreeChangesNoGenesis.TryGetSlot(200, out _), Is.False);
        }
    }

    [TestCase(148UL, 205UL, 206UL)]
    [TestCase(148UL, 200UL, 200UL)]
    [TestCase(148UL, 231UL, 238UL)]
    [TestCase(230UL, 0UL, 238UL)]
    [TestCase(199UL, 199UL, 209UL)]
    public void Adding_slots_until_a_target_crosses_every_boundary(ulong timestamp, ulong target, ulong expected) =>
        Assert.That(ThreeChangesGrid.AddSlotsUntil(timestamp, target), Is.EqualTo(expected));

    [TestCase(224UL, true)]
    [TestCase(225UL, false)]
    [TestCase(230UL, true)]
    [TestCase(234UL, false)]
    public void Slot_starts_follow_every_segment(ulong timestamp, bool expected) =>
        Assert.That(ThreeChangesGrid.IsSlotStart(timestamp), Is.EqualTo(expected));

    [Test]
    public void Later_entry_at_or_before_genesis_replaces_the_genesis_length()
    {
        SlotSchedule schedule = new(12, Genesis, [(0, 6_000), (Genesis, 8_000), (Genesis + 10, 10_000)]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetSecondsPerSlot(Genesis), Is.EqualTo(8UL));
            Assert.That(schedule.TryGetSlot(Genesis + 16, out ulong slot) && slot == 2, Is.True);
            Assert.That(schedule.GetSecondsPerSlot(Genesis + 16), Is.EqualTo(10UL));
        }
    }

    [TestCase(new ulong[] { 200, 160 }, new ulong[] { 10_000, 6_000 }, TestName = "Unsorted")]
    [TestCase(new ulong[] { 160, 160 }, new ulong[] { 10_000, 6_000 }, TestName = "Duplicate timestamp")]
    [TestCase(new ulong[] { 160 }, new ulong[] { 0 }, TestName = "Zero duration")]
    [TestCase(new ulong[] { 160 }, new ulong[] { 10_500 }, TestName = "Not whole seconds")]
    public void Invalid_schedule_is_rejected(ulong[] timestamps, ulong[] durations)
    {
        (ulong, ulong)[] schedule = [.. timestamps.Select((t, i) => (t, durations[i]))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => SlotSchedule.ValidateSchedule(schedule), Throws.InstanceOf<System.ArgumentException>());
            Assert.That(() => new SlotSchedule(12, Genesis, schedule), Throws.InstanceOf<System.ArgumentException>());
        }
    }

    [Test]
    public void Eip8198_without_a_chain_slot_duration_keeps_the_configured_length()
    {
        CustomSpecProvider provider = new(((ForkActivation)0, London.Instance), (new ForkActivation(0, Fork), Bogota.Instance))
        {
            BeaconChainGenesisTimestamp = Genesis
        };
        SlotSchedule schedule = provider.GetSlotSchedule(12);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetSecondsPerSlot(Fork - 1), Is.EqualTo(12UL));
            Assert.That(schedule.GetSecondsPerSlot(Fork), Is.EqualTo(12UL));
            Assert.That(schedule.GetSlotTimestamp(6), Is.EqualTo(Fork + 12));
        }
    }

    [Test]
    public void Chain_slot_duration_applies_from_eip8198_however_it_is_enabled()
    {
        CustomSpecProvider bogota = new(((ForkActivation)0, London.Instance), (new ForkActivation(0, Fork), Bogota.Instance))
        {
            BeaconChainGenesisTimestamp = Genesis,
            Eip8198SlotDurationMs = 10_000
        };
        CustomSpecProvider flagOnly = new(
            ((ForkActivation)0, London.Instance),
            (new ForkActivation(0, Fork), new OverridableReleaseSpec(London.Instance) { IsEip8198Enabled = true }))
        {
            BeaconChainGenesisTimestamp = Genesis,
            Eip8198SlotDurationMs = 10_000
        };

        using (Assert.EnterMultipleScope())
        {
            foreach (SlotSchedule schedule in new[] { bogota.GetSlotSchedule(12), flagOnly.GetSlotSchedule(12) })
            {
                Assert.That(schedule.GetSecondsPerSlot(Fork - 1), Is.EqualTo(12UL));
                Assert.That(schedule.GetSecondsPerSlot(Fork), Is.EqualTo(10UL));
                Assert.That(schedule.GetSlotTimestamp(6), Is.EqualTo(Fork + 10));
            }
        }
    }

    [Test]
    public void Chain_slot_duration_overrides_the_eip8198_length()
    {
        CustomSpecProvider provider = new(((ForkActivation)0, London.Instance), (new ForkActivation(0, Fork), Bogota.Instance))
        {
            BeaconChainGenesisTimestamp = Genesis,
            Eip8198SlotDurationMs = 6_000
        };
        SlotSchedule schedule = provider.GetSlotSchedule(12);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetSecondsPerSlot(Fork - 1), Is.EqualTo(12UL));
            Assert.That(schedule.GetSecondsPerSlot(Fork), Is.EqualTo(6UL));
        }
    }

    [Test]
    public void Each_fork_that_changes_the_slot_length_starts_a_segment()
    {
        // A later fork that drops EIP-8198 stands in for one that sets another slot length.
        CustomSpecProvider provider = new(
            ((ForkActivation)0, London.Instance),
            (new ForkActivation(0, 130), Amsterdam.Instance),
            (new ForkActivation(0, Fork), Bogota.Instance),
            (new ForkActivation(0, 190), new OverridableReleaseSpec(Bogota.Instance) { IsEip7805Enabled = false }),
            (new ForkActivation(0, 200), Amsterdam.Instance))
        {
            BeaconChainGenesisTimestamp = Genesis,
            Eip8198SlotDurationMs = 10_000
        };
        SlotSchedule schedule = provider.GetSlotSchedule(12);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetSecondsPerSlot(159), Is.EqualTo(12UL));
            Assert.That(schedule.GetSecondsPerSlot(160), Is.EqualTo(10UL));
            Assert.That(schedule.GetSecondsPerSlot(199), Is.EqualTo(10UL));
            Assert.That(schedule.GetSecondsPerSlot(200), Is.EqualTo(12UL));
            Assert.That(schedule.TryGetSlot(212, out ulong slot) && slot == 10, Is.True);
        }
    }

    [Test]
    public void Spec_provider_without_eip8198_keeps_the_configured_length()
    {
        CustomSpecProvider provider = new(((ForkActivation)0, London.Instance), (new ForkActivation(0, Fork), Amsterdam.Instance))
        {
            BeaconChainGenesisTimestamp = Genesis
        };
        SlotSchedule schedule = provider.GetSlotSchedule(12);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetSecondsPerSlot(ulong.MaxValue), Is.EqualTo(12UL));
            Assert.That(schedule.AddSlots(148, 3), Is.EqualTo(184UL));
            Assert.That(schedule.GetSlotTimestamp(6), Is.EqualTo(172UL));
        }
    }

    [Test]
    public void Mainnet_unscheduled_bogota_keeps_the_configured_slot_duration() =>
        Assert.That(MainnetSpecProvider.Instance.GetSlotSchedule(12).GetSecondsPerSlot(1_792_416_379), Is.EqualTo(12UL));
}
