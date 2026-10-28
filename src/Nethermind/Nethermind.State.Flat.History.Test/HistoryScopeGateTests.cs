// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
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
    public void TryDrainForFloorAdvance_AScopeOpenAcrossSeveralAdvances_IsWaitedOnUntilItCloses()
    {
        HistoryScopeGate gate = new();
        long stuckScope = gate.EnterScope();

        for (int advance = 0; advance < 3; advance++)
        {
            gate.ExitScope(gate.EnterScope());
            Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.Zero, CancellationToken.None), Is.False,
                "generations that emptied in between must not hide the older one the stuck scope is counted on");
        }

        gate.ExitScope(stuckScope);
        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True);
    }

    [Test]
    public void TryDrain_WithScopesOpeningAndClosingOnManyThreads_WaitsOnlyForScopesOpenedBeforeTheAdvance()
    {
        HistoryScopeGate gate = new();
        long stuckScope = gate.EnterScope();
        using CancellationTokenSource stop = new();
        Task[] churn = new Task[Math.Max(2, Environment.ProcessorCount)];
        for (int i = 0; i < churn.Length; i++)
        {
            churn[i] = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested) gate.ExitScope(gate.EnterScope());
            });
        }

        long generation = 0;
        for (int advance = 0; advance < 100; advance++) generation = gate.BeginFloorAdvance();

        bool drainedWhileStuck = gate.TryDrain(generation, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        gate.ExitScope(stuckScope);
        bool drainedAfterClose = gate.TryDrain(generation, TimeSpan.FromSeconds(5), CancellationToken.None);
        stop.Cancel();
        Task.WaitAll(churn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drainedWhileStuck, Is.False, "the scope opened before every advance must hold the drain");
            Assert.That(drainedAfterClose, Is.True, "scopes still opening on other threads join the newest generation and must not hold it");
            Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True,
                "every scope closed, so none may be left counted");
        }
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
