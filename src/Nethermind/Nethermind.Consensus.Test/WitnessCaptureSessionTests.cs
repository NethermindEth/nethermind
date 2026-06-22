// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Consensus.Stateless;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class WitnessCaptureSessionTests
{
    [Test]
    public void Starts_inactive()
    {
        WitnessCaptureSession session = new();

        Assert.That(session.IsActive, Is.False);
        Assert.That(session.WorldStateRecorder, Is.Null);
        Assert.That(session.HeaderRecorder, Is.Null);
    }

    [Test]
    public void TryArm_installs_both_recorders()
    {
        WitnessCaptureSession session = new();
        WitnessHeaderRecorder header = new();
        WitnessGeneratingWorldState world = NewWorldState(header);

        Assert.That(session.TryArm(world, header), Is.True);
        Assert.That(session.IsActive, Is.True);
        Assert.That(session.WorldStateRecorder, Is.SameAs(world));
        Assert.That(session.HeaderRecorder, Is.SameAs(header));
    }

    [Test]
    public void TryArm_on_active_session_loses_without_clobbering_the_winner()
    {
        // Regression: the recorders are published as one atomic slot, so a losing armer must leave
        // the winner's pair fully intact — never a winner world-state paired with a loser's header.
        WitnessCaptureSession session = new();
        WitnessHeaderRecorder winnerHeader = new();
        WitnessHeaderRecorder loserHeader = new();
        WitnessGeneratingWorldState winnerWorld = NewWorldState(winnerHeader);
        WitnessGeneratingWorldState loserWorld = NewWorldState(loserHeader);

        Assert.That(session.TryArm(winnerWorld, winnerHeader), Is.True);
        Assert.That(session.TryArm(loserWorld, loserHeader), Is.False);

        Assert.That(session.WorldStateRecorder, Is.SameAs(winnerWorld));
        Assert.That(session.HeaderRecorder, Is.SameAs(winnerHeader));
    }

    [Test]
    public void Disarm_clears_the_slot_and_allows_rearming()
    {
        WitnessCaptureSession session = new();
        WitnessHeaderRecorder header = new();
        session.TryArm(NewWorldState(header), header);

        session.Disarm();

        Assert.That(session.IsActive, Is.False);
        Assert.That(session.WorldStateRecorder, Is.Null);
        Assert.That(session.HeaderRecorder, Is.Null);

        WitnessHeaderRecorder header2 = new();
        Assert.That(session.TryArm(NewWorldState(header2), header2), Is.True, "re-arm after disarm must succeed");
    }

    // The session only stores the references, so the world-state dependencies are never touched.
    private static WitnessGeneratingWorldState NewWorldState(WitnessHeaderRecorder headerRecorder)
        => new(null!, null!, null!, headerRecorder, null!);
}
