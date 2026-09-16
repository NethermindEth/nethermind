// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

[TestFixture]
[NonParallelizable]
public class SyncTimeInModeTrackerTests
{
    private static readonly long Freq = Stopwatch.Frequency;

    private long _now;

    [SetUp]
    public void SetUp() => Metrics.SyncTimeInModeSeconds.Clear();

    private (SyncTimeInModeTracker Tracker, ISyncModeSelector Selector) CreateTracker(SyncMode initialMode = SyncMode.None)
    {
        _now = 0;
        ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
        selector.Current.Returns(initialMode);
        SyncTimeInModeTracker tracker = new(selector, () => _now);
        return (tracker, selector);
    }

    private void Advance(double seconds) => _now += (long)(seconds * Freq);

    [Test]
    public void Accumulates_wall_clock_for_the_active_mode()
    {
        (SyncTimeInModeTracker tracker, ISyncModeSelector selector) = CreateTracker();

        selector.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.None, SyncMode.FastHeaders));
        Advance(5);
        tracker.UpdateMetrics();

        Assert.That(Metrics.SyncTimeInModeSeconds[SyncMode.FastHeaders], Is.EqualTo(5));
    }

    [Test]
    public void Attributes_time_to_every_overlapping_mode()
    {
        (SyncTimeInModeTracker tracker, ISyncModeSelector selector) = CreateTracker();

        selector.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.None, SyncMode.FastBodies | SyncMode.FastReceipts));
        Advance(3);
        tracker.UpdateMetrics();

        Assert.That(Metrics.SyncTimeInModeSeconds[SyncMode.FastBodies], Is.EqualTo(3));
        Assert.That(Metrics.SyncTimeInModeSeconds[SyncMode.FastReceipts], Is.EqualTo(3));
        // The composite FastBlocks bit must not be double-counted as its own leaf stage.
        Assert.That(Metrics.SyncTimeInModeSeconds[SyncMode.FastHeaders], Is.EqualTo(0));
    }

    [Test]
    public void Tracks_not_syncing_modes_separately_from_syncing_modes()
    {
        (SyncTimeInModeTracker tracker, ISyncModeSelector selector) = CreateTracker();

        selector.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.None, SyncMode.WaitingForBlock));
        Advance(2);
        selector.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.WaitingForBlock, SyncMode.Full));
        Advance(7);
        tracker.UpdateMetrics();

        Assert.That(Metrics.SyncTimeInModeSeconds[SyncMode.WaitingForBlock], Is.EqualTo(2));
        Assert.That(Metrics.SyncTimeInModeSeconds[SyncMode.Full], Is.EqualTo(7));
    }

    [Test]
    public void Keeps_accumulating_across_ticks_within_the_same_mode()
    {
        (SyncTimeInModeTracker tracker, ISyncModeSelector selector) = CreateTracker();

        selector.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.None, SyncMode.StateNodes));
        Advance(4);
        tracker.UpdateMetrics();
        Advance(6);
        tracker.UpdateMetrics();

        Assert.That(Metrics.SyncTimeInModeSeconds[SyncMode.StateNodes], Is.EqualTo(10));
    }
}
