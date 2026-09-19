// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public class ForkChoiceStoreTests
{
    [Test]
    public void On_tick_resets_proposer_boost_and_pulls_up_unrealized_checkpoints_at_epoch_boundary()
    {
        ForkChoiceStore store = new(slotsPerEpoch: 8, currentSlot: 9, GetCheckpoint(1), GetCheckpoint(0));

        store.ProposerBoostRoot = GetRoot(5);
        store.UpdateUnrealizedCheckpoints(GetCheckpoint(2), GetCheckpoint(1));

        // Ticking to the current (or an earlier) slot is a no-op.
        store.OnTick(9);
        Assert.That(store.ProposerBoostRoot, Is.EqualTo(GetRoot(5)), "boost survives a no-op tick");

        // A new slot inside the epoch resets the boost but does not pull up checkpoints.
        store.OnTick(10);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.ProposerBoostRoot, Is.EqualTo(Hash256.Zero), "boost reset at slot start");
            Assert.That(store.JustifiedCheckpoint, Is.EqualTo(GetCheckpoint(1)), "no pull-up mid-epoch");
            Assert.That(store.FinalizedCheckpoint, Is.EqualTo(GetCheckpoint(0)), "no pull-up mid-epoch");
        }

        // Crossing the epoch boundary (slot 16) pulls the unrealized checkpoints up.
        store.OnTick(17);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.CurrentSlot, Is.EqualTo(17ul));
            Assert.That(store.CurrentEpoch, Is.EqualTo(2ul));
            Assert.That(store.JustifiedCheckpoint, Is.EqualTo(GetCheckpoint(2)), "justified pulled up");
            Assert.That(store.FinalizedCheckpoint, Is.EqualTo(GetCheckpoint(1)), "finalized pulled up");
        }

        // Checkpoint updates are monotonic by epoch: stale candidates are ignored.
        store.UpdateCheckpoints(GetCheckpoint(1), GetCheckpoint(0));
        store.UpdateUnrealizedCheckpoints(GetCheckpoint(1), GetCheckpoint(0));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.JustifiedCheckpoint, Is.EqualTo(GetCheckpoint(2)), "stale justified ignored");
            Assert.That(store.FinalizedCheckpoint, Is.EqualTo(GetCheckpoint(1)), "stale finalized ignored");
            Assert.That(store.UnrealizedJustifiedCheckpoint, Is.EqualTo(GetCheckpoint(2)), "stale unrealized justified ignored");
            Assert.That(store.UnrealizedFinalizedCheckpoint, Is.EqualTo(GetCheckpoint(1)), "stale unrealized finalized ignored");
        }
    }
}
