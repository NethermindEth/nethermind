// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryScopeGateTests
{
    [Test]
    public void TryDrainForFloorAdvance_WithNoOpenScopes_SucceedsImmediately()
    {
        HistoryScopeGate gate = new();

        bool drained = gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.That(drained, Is.True);
    }

    [Test]
    public void TryDrainForFloorAdvance_AScopeClosedBeforeTheDrainCall_DoesNotBlockIt()
    {
        HistoryScopeGate gate = new();
        int epoch = gate.EnterScope();
        gate.ExitScope(epoch);

        bool drained = gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.That(drained, Is.True, "a scope that closed before the drain call started must not block it");
    }

    [Test]
    public void TryDrainForFloorAdvance_TimesOutWhileAScopeStaysOpen_AndRestoresTheEpoch()
    {
        HistoryScopeGate gate = new();
        int stuckEpoch = gate.EnterScope();

        bool drained = gate.TryDrainForFloorAdvance(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.That(drained, Is.False, "a scope that never closes must time out the drain, not hang forever");

        // Restoration is observable: a scope entering now must join the SAME epoch as the stuck one, so a later
        // retry keeps waiting on the one census that actually contains the stuck scope, instead of a flip having
        // moved on to an unrelated (and trivially already-empty) slot.
        int newEpoch = gate.EnterScope();
        Assert.That(newEpoch, Is.EqualTo(stuckEpoch),
            "after a timed-out drain, new scopes must keep joining the un-drained epoch until a retry actually succeeds");

        gate.ExitScope(stuckEpoch);
        gate.ExitScope(newEpoch);
    }

    [Test]
    public void TryDrainForFloorAdvance_AfterASuccessfulDrain_ANewScopeJoinsTheOtherEpochAndDoesNotBlockIt()
    {
        HistoryScopeGate gate = new();

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True,
            "precondition: draining an empty gate succeeds and flips the epoch");

        int epoch = gate.EnterScope();
        bool secondDrain = gate.TryDrainForFloorAdvance(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.That(secondDrain, Is.False, "a scope opened into the new epoch must be waited on by the next drain, not skipped");

        gate.ExitScope(epoch);
    }

    [Test]
    public void EnterScope_ExitScope_NeverGoesNegativeUnderSequentialUse()
    {
        HistoryScopeGate gate = new();
        int first = gate.EnterScope();
        int second = gate.EnterScope();
        gate.ExitScope(first);
        gate.ExitScope(second);

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True);
    }
}
