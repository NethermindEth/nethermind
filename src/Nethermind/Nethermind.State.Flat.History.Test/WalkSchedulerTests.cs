// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.State.Flat.History.Walk;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class WalkSchedulerTests
{
    [Test]
    public void Fork_CompletedByAnotherJoinWhileItsOwnJoinIsStillOpen_ReleasesWhatItsWorkCaptured()
    {
        WalkScheduler scheduler = new(workers: 1, CancellationToken.None);
        WalkJoin helped = new(scheduler);
        WalkJoin owner = new(scheduler);
        helped.Fork(static () => { });
        WeakReference captured = ForkCapturingPayload(owner);

        scheduler.HelpUntilDone(helped);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.That(captured.IsAlive, Is.False, "a finished fork must not keep its work's captures alive until the owner joins");
        Assert.That(owner.IsDone, Is.True);
        owner.Join(null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ForkCapturingPayload(WalkJoin join)
    {
        byte[] payload = new byte[1024];
        join.Fork(() => GC.KeepAlive(payload));
        return new WeakReference(payload);
    }
}
