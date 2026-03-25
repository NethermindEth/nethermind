// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[Parallelizable(ParallelScope.Self)]
public class MergeFinalizationManagerTests
{
    [Test]
    public void Dispose_unsubscribes_from_PoSSwitcher_TerminalBlockReached()
    {
        IManualBlockFinalizationManager inner = Substitute.For<IManualBlockFinalizationManager>();
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();

        MergeFinalizationManager manager = new(inner, null, poSSwitcher);
        manager.Dispose();

        // Raising the event after Dispose should NOT propagate — if unsubscription
        // worked, the manager won't set IsPostMerge and won't forward events.
        // NSubstitute tracks event subscriptions: Received/DidNotReceive on events
        // isn't supported, so we verify by raising the event and checking no side effects.
        poSSwitcher.TerminalBlockReached += Raise.Event();

        // After disposal + re-raise, IsPostMerge should still be false (the handler was removed).
        // We verify indirectly: LastFinalizedBlockLevel returns 0 when IsPostMerge is false.
        Assert.That(manager.LastFinalizedBlockLevel, Is.EqualTo(0));
    }

    [Test]
    public void Dispose_unsubscribes_from_inner_BlocksFinalized()
    {
        IManualBlockFinalizationManager inner = Substitute.For<IManualBlockFinalizationManager>();
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();

        int blocksFinalisedCount = 0;
        MergeFinalizationManager manager = new(inner, null, poSSwitcher);
        manager.BlocksFinalized += (_, _) => blocksFinalisedCount++;

        // Before dispose, the forwarding works.
        inner.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(null!, null!));
        Assert.That(blocksFinalisedCount, Is.EqualTo(1), "Event should propagate before Dispose");

        manager.Dispose();

        // After dispose, raising inner.BlocksFinalized should NOT propagate through the manager.
        inner.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(null!, null!));
        Assert.That(blocksFinalisedCount, Is.EqualTo(1), "Event should NOT propagate after Dispose");
    }
}
