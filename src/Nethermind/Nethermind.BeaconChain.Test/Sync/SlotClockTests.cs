// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

public class SlotClockTests
{
    private const ulong GenesisTime = 1_606_824_023;

    internal static BeaconChainSpec CreateSpec(ulong genesisTime, ulong secondsPerSlot = 12) => new()
    {
        SecondsPerSlot = secondsPerSlot,
        SlotsPerEpoch = 32,
        GenesisTime = genesisTime,
        GenesisValidatorsRoot = Hash256.Zero,
        Forks = [new(Bytes.FromHexString("0x00000000"), 0)],
        BlobSchedule = [],
        ElectraForkEpoch = 0,
        FuluForkEpoch = ulong.MaxValue,
        MaxBlobsPerBlockElectra = 9,
    };

    private static SlotClock CreateClock(long millisecondsSinceGenesis) =>
        new(CreateSpec(GenesisTime), new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(GenesisTime).AddMilliseconds(millisecondsSinceGenesis)));

    [TestCase(0L, 0ul, 0ul, 0L, 12_000L, TestName = "at genesis")]
    [TestCase(5_500L, 0ul, 0ul, 5_500L, 6_500L, TestName = "mid first slot")]
    [TestCase(12_000L, 1ul, 0ul, 0L, 12_000L, TestName = "second slot start")]
    [TestCase(-3_000L, 0ul, 0ul, 0L, 3_000L, TestName = "before genesis")]
    [TestCase(12_000L * 32 * 100 + 3_000, 3_200ul, 100ul, 3_000L, 9_000L, TestName = "far future")]
    public void Computes_slot_math(long millisecondsSinceGenesis, ulong slot, ulong epoch, long intoSlotMs, long toNextSlotMs)
    {
        SlotClock clock = CreateClock(millisecondsSinceGenesis);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.CurrentSlot, Is.EqualTo(slot), "current slot");
            Assert.That(clock.CurrentEpoch, Is.EqualTo(epoch), "current epoch");
            Assert.That(clock.TimeIntoSlot, Is.EqualTo(TimeSpan.FromMilliseconds(intoSlotMs)), "time into slot");
            Assert.That(clock.MillisecondsToNextSlot, Is.EqualTo(toNextSlotMs), "ms to next slot");
            Assert.That(clock.UnixMilliseconds, Is.EqualTo((long)GenesisTime * 1000 + millisecondsSinceGenesis), "unix ms");
        }
    }

    [TestCase(0ul)]
    [TestCase(5ul)]
    [TestCase(13_410_304ul)]
    public void Slot_start_time_is_genesis_plus_slot_duration(ulong slot) =>
        Assert.That(CreateClock(0).SlotStartMilliseconds(slot), Is.EqualTo((long)GenesisTime * 1000 + (long)slot * 12_000));

    [Test]
    [CancelAfter(30_000)]
    public async Task Slot_ticks_fire_consecutive_slots_and_start_at_slot_zero_before_genesis(CancellationToken token)
    {
        // One-second slots anchored at the wall clock so ticks arrive quickly; assertions are
        // relative (consecutive boundaries) so scheduling jitter cannot make them flaky.
        ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        List<ulong> ticks = await CollectTicksAsync(new SlotClock(CreateSpec(now, secondsPerSlot: 1), Timestamper.Default), 2, token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ticks[0], Is.GreaterThanOrEqualTo(1ul), "the first boundary after genesis starts slot >= 1");
            Assert.That(ticks[1], Is.GreaterThan(ticks[0]), "ticks advance");
        }

        // Recapture the wall clock: collecting the ticks above consumed real time.
        ulong preGenesis = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3;
        SlotClock preGenesisClock = new(CreateSpec(preGenesis, secondsPerSlot: 1), Timestamper.Default);
        Assert.That((await CollectTicksAsync(preGenesisClock, 1, token))[0], Is.EqualTo(0ul), "first tick before genesis is slot 0");
    }

    private static async Task<List<ulong>> CollectTicksAsync(SlotClock clock, int count, CancellationToken token)
    {
        List<ulong> ticks = [];
        await foreach (ulong slot in clock.SlotTicks(token))
        {
            ticks.Add(slot);
            if (ticks.Count == count)
            {
                break;
            }
        }

        return ticks;
    }
}
