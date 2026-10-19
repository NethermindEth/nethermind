// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    private const ulong SlotDurationMs = 10_000;

    private static readonly SlotSchedule Scheduled = new(12, Genesis, Fork, SlotDurationMs);
    private static readonly SlotSchedule Unscheduled = new(12, Genesis);
    private static readonly SlotSchedule NoGenesis = new(12, null, Fork, SlotDurationMs);

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
        SlotSchedule schedule = new(12, Genesis, Fork + 5, SlotDurationMs);
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
        SlotSchedule schedule = new(12, Genesis, Genesis - 50, SlotDurationMs);
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

    [Test]
    public void Spec_provider_reports_the_eip8198_activation()
    {
        IReleaseSpec enabled = Bogota.Instance;
        CustomSpecProvider scheduled = new(
            ((ForkActivation)0, London.Instance),
            (new ForkActivation(0, 1_000), Amsterdam.Instance),
            (new ForkActivation(0, 2_000), enabled))
        {
            Eip8198SlotDurationMs = SlotDurationMs
        };
        CustomSpecProvider unscheduled = new(((ForkActivation)0, London.Instance), (new ForkActivation(0, 1_000), Amsterdam.Instance));
        CustomSpecProvider atGenesis = new(((ForkActivation)0, enabled));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scheduled.GetEip8198Timestamp(), Is.EqualTo(2_000UL));
            Assert.That(unscheduled.GetEip8198Timestamp(), Is.Null);
            Assert.That(atGenesis.GetEip8198Timestamp(), Is.EqualTo(0UL));
            Assert.That(scheduled.GetSlotSchedule(12).GetSecondsPerSlot(2_000), Is.EqualTo(10UL));
            Assert.That(unscheduled.GetSlotSchedule(12).GetSecondsPerSlot(2_000), Is.EqualTo(12UL));
        }
    }

    [Test]
    public void Spec_provider_slot_duration_reaches_the_schedule()
    {
        IReleaseSpec enabled = Bogota.Instance;
        CustomSpecProvider provider = new(((ForkActivation)0, London.Instance), (new ForkActivation(0, 2_000), enabled))
        {
            Eip8198SlotDurationMs = 6_000
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.GetSlotSchedule(12).GetSecondsPerSlot(1_999), Is.EqualTo(12UL));
            Assert.That(provider.GetSlotSchedule(12).GetSecondsPerSlot(2_000), Is.EqualTo(6UL));
        }
    }

    // Genesis 100, fork at slot 5 (160); pre-fork slots are 12 s.
    [TestCase(6_000UL, 159UL, 4UL, 148UL, 171UL, 177UL, 172UL)]
    [TestCase(6_000UL, 172UL, 7UL, 172UL, 178UL, 184UL, 172UL)]
    [TestCase(12_000UL, 172UL, 6UL, 172UL, 184UL, 196UL, 172UL)]
    [TestCase(8_000UL, 175UL, 6UL, 168UL, 183UL, 191UL, 176UL)]
    public void Non_default_slot_duration_sets_the_post_fork_grid(
        ulong slotDurationMs, ulong timestamp, ulong slot, ulong slotStart, ulong next, ulong twoSlotsOn, ulong untilTarget)
    {
        SlotSchedule schedule = new(12, Genesis, Fork, slotDurationMs);
        SlotSchedule noGenesis = new(12, null, Fork, slotDurationMs);
        ulong seconds = slotDurationMs / 1_000;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetSecondsPerSlot(Fork), Is.EqualTo(seconds));
            Assert.That(schedule.GetSecondsPerSlot(Fork - 1), Is.EqualTo(12UL));
            Assert.That(schedule.TryGetSlot(timestamp, out ulong actualSlot), Is.True);
            Assert.That(actualSlot, Is.EqualTo(slot));
            Assert.That(schedule.GetSlotTimestamp(slot), Is.EqualTo(slotStart));
            Assert.That(schedule.IsSlotStart(Fork + seconds), Is.True);
            Assert.That(schedule.GetNextSlotTimestamp(timestamp), Is.EqualTo(next));
            Assert.That(schedule.AddSlots(timestamp, 2), Is.EqualTo(twoSlotsOn));
            Assert.That(schedule.AddSlotsUntil(148, 170), Is.EqualTo(untilTarget));
            Assert.That(noGenesis.AddSlots(timestamp, 2), Is.EqualTo(twoSlotsOn));
        }
    }

    [TestCase(0UL)]
    [TestCase(500UL)]
    [TestCase(10_500UL)]
    public void Slot_duration_that_is_not_whole_seconds_is_rejected(ulong slotDurationMs) =>
        Assert.That(() => new SlotSchedule(12, Genesis, Fork, slotDurationMs), Throws.TypeOf<System.ArgumentOutOfRangeException>());

    [Test]
    public void Spec_provider_without_eip8198_needs_no_slot_duration()
    {
        CustomSpecProvider provider = new(((ForkActivation)0, London.Instance)) { BeaconChainGenesisTimestamp = Genesis };
        SlotSchedule schedule = provider.GetSlotSchedule(12);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Eip8198SlotDurationMs, Is.Null);
            Assert.That(schedule.GetSecondsPerSlot(ulong.MaxValue), Is.EqualTo(12UL));
            Assert.That(schedule.AddSlots(148, 3), Is.EqualTo(184UL));
            Assert.That(schedule.GetSlotTimestamp(6), Is.EqualTo(172UL));
        }
    }

    [Test]
    public void Mainnet_unscheduled_bogota_keeps_the_configured_slot_duration()
    {
        ISpecProvider provider = MainnetSpecProvider.Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.GetEip8198Timestamp(), Is.Null);
            Assert.That(provider.GetSlotSchedule(12).GetSecondsPerSlot(1_792_416_379), Is.EqualTo(12UL));
        }
    }

    [Test]
    public void Spec_provider_scheduling_eip8198_without_slot_duration_throws()
    {
        IReleaseSpec enabled = Bogota.Instance;
        CustomSpecProvider provider = new(((ForkActivation)0, London.Instance), (new ForkActivation(0, 2_000), enabled));

        Assert.That(() => provider.GetSlotSchedule(12),
            Throws.InvalidOperationException.With.Message.Contains("eip8198SlotDurationMs"));
    }
}
