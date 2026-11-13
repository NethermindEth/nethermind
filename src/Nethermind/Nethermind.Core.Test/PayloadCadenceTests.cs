// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Memory;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[Parallelizable(ParallelScope.All)]
public class PayloadCadenceTests
{
    /// <summary>Starts payloads every <paramref name="intervalMs"/>, each in processing for <paramref name="processingMs"/>.</summary>
    private static long Run(PayloadCadence cadence, long startMs, int payloads, long intervalMs, long processingMs)
    {
        for (int i = 0; i < payloads; i++)
        {
            cadence.OnPayloadStart(startMs, idle: true);
            cadence.OnIdle(startMs + processingMs);
            startMs += intervalMs;
        }
        return startMs;
    }

    [Test]
    public void Medians_wait_for_a_full_window()
    {
        PayloadCadence cadence = new();
        long next = Run(cadence, 0, PayloadCadence.Samples, 200, 40);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cadence.MedianIntervalMs, Is.EqualTo(-1), "the first payload has no interval");
            Assert.That(cadence.MedianIdleGapMs, Is.EqualTo(-1));
        }

        Run(cadence, next, 1, 200, 40);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cadence.MedianIntervalMs, Is.EqualTo(200));
            Assert.That(cadence.MedianIdleGapMs, Is.EqualTo(160));
        }
    }

    [Test]
    public void Median_follows_a_change_of_cadence_within_a_window()
    {
        PayloadCadence cadence = new();
        long next = Run(cadence, 0, 2 * PayloadCadence.Samples, 2_000, 200);
        Assert.That(cadence.MedianIdleGapMs, Is.EqualTo(1_800));

        // The first of these payloads still comes 2 s after the last: six 200 ms intervals of 16 leave the median at 2 s,
        // nine move it.
        next = Run(cadence, next, 7, 200, 40);
        Assert.That(cadence.MedianIntervalMs, Is.EqualTo(2_000));
        Run(cadence, next, 3, 200, 40);
        Assert.That(cadence.MedianIntervalMs, Is.EqualTo(200));
    }

    [Test]
    public void Payload_starting_while_another_is_in_processing_leaves_no_idle_gap()
    {
        PayloadCadence cadence = new();
        cadence.OnPayloadStart(0, idle: true);
        cadence.OnIdle(40);
        for (int i = 1; i <= PayloadCadence.Samples; i++) cadence.OnPayloadStart(i * 200, idle: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cadence.MedianIdleGapMs, Is.Zero);
            Assert.That(cadence.MedianIntervalMs, Is.EqualTo(200));
        }
    }

    [Test]
    public void Samples_longer_than_a_minute_are_left_out()
    {
        PayloadCadence cadence = new();
        long next = Run(cadence, 0, PayloadCadence.Samples, 12_000, 500);
        // Back from a two-minute stall: neither the interval nor the idle gap before this payload is a sample.
        next = Run(cadence, next + 120_000, 1, 12_000, 500);
        Assert.That(cadence.MedianIntervalMs, Is.EqualTo(-1), "15 samples so far");
        Run(cadence, next, 1, 12_000, 500);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cadence.MedianIntervalMs, Is.EqualTo(12_000));
            Assert.That(cadence.MedianIdleGapMs, Is.EqualTo(11_500));
        }
    }
}
