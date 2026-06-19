// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class StackAccessTrackerTests
{
    [Test]
    public void Consecutive_warm_up_of_same_storage_cell_returns_warm()
    {
        using StackAccessTracker tracker = new();
        StorageCell cell = new(TestItem.AddressA, 1);

        Assert.That(tracker.WarmUp(in cell), Is.True);
        Assert.That(tracker.IsCold(in cell), Is.False);
        Assert.That(tracker.WarmUp(in cell), Is.False);
    }

    [Test]
    public void Restore_clears_storage_cell_warmed_after_snapshot()
    {
        using StackAccessTracker tracker = new();
        StorageCell cell = new(TestItem.AddressA, 1);

        tracker.TakeSnapshot();
        Assert.That(tracker.WarmUp(in cell), Is.True);

        tracker.Restore();

        Assert.That(tracker.IsCold(in cell), Is.True);
        Assert.That(tracker.WarmUp(in cell), Is.True);
    }

    [Test]
    public void Tracing_access_restore_keeps_storage_cell_warmed_after_snapshot()
    {
        using StackAccessTracker tracker = new(isTracingAccess: true);
        StorageCell cell = new(TestItem.AddressA, 1);

        tracker.TakeSnapshot();
        Assert.That(tracker.WarmUp(in cell), Is.True);

        tracker.Restore();

        Assert.That(tracker.IsCold(in cell), Is.False);
        Assert.That(tracker.WarmUp(in cell), Is.False);
    }
}
