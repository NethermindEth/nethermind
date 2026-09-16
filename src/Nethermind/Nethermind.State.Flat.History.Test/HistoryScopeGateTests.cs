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
        long scope = gate.EnterScope();
        gate.ExitScope(scope);

        bool drained = gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.That(drained, Is.True, "a scope that closed before the drain call started must not block it");
    }

    [Test]
    public void TryDrainForFloorAdvance_TimesOutWhileAScopeStaysOpen()
    {
        HistoryScopeGate gate = new();
        long stuckScope = gate.EnterScope();

        bool drained = gate.TryDrainForFloorAdvance(TimeSpan.Zero, CancellationToken.None);

        Assert.That(drained, Is.False, "a scope that never closes must time out the drain, not hang forever");

        gate.ExitScope(stuckScope);
    }

    [Test]
    public void TryDrainForFloorAdvance_AfterATimedOutDrain_ScopesThatOpenedAndClosedSinceDoNotBlockTheRetry()
    {
        HistoryScopeGate gate = new();
        long stuckScope = gate.EnterScope();

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.Zero, CancellationToken.None), Is.False,
            "precondition: the stuck scope times the first drain out");

        gate.ExitScope(gate.EnterScope());
        gate.ExitScope(stuckScope);

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True,
            "once every scope has closed, a retry must succeed - a timed-out drain must not leave the gate waiting on the slot new scopes keep joining");
    }

    [Test]
    public void TryDrainForFloorAdvance_AStuckScope_IsStillWaitedOnByEveryRetry()
    {
        HistoryScopeGate gate = new();
        long stuckScope = gate.EnterScope();

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.Zero, CancellationToken.None), Is.False);
        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.Zero, CancellationToken.None), Is.False,
            "the stuck scope was admitted under an older floor generation, so a retry must keep waiting on it");

        gate.ExitScope(stuckScope);
        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True);
    }

    [Test]
    public void TryDrainForFloorAdvance_AfterASuccessfulDrain_ANewScopeIsWaitedOnByTheNextDrain()
    {
        HistoryScopeGate gate = new();

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True,
            "precondition: draining an empty gate succeeds");

        long scope = gate.EnterScope();
        bool secondDrain = gate.TryDrainForFloorAdvance(TimeSpan.Zero, CancellationToken.None);

        Assert.That(secondDrain, Is.False, "a scope opened after the drain must be waited on by the next one, not skipped");

        gate.ExitScope(scope);
    }

    [Test]
    public void EnterScope_ExitScope_NeverGoesNegativeUnderSequentialUse()
    {
        HistoryScopeGate gate = new();
        long first = gate.EnterScope();
        long second = gate.EnterScope();
        gate.ExitScope(first);
        gate.ExitScope(second);

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True);
    }
}
