// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

public class PostStateCacheTests
{
    /// <summary>
    /// A trusted replay applies a block to the lineage state in place and keeps a copy of the parent's state, then fork choice replays
    /// the block's body votes. A vote whose target is the parent (the epoch's checkpoint block) must be checked against the parent's
    /// state (specs/phase0/fork-choice.md store_target_checkpoint_state), not the half-imported child's one the lineage already holds.
    /// </summary>
    [Test]
    public void Retained_copy_of_the_lineage_root_wins_over_the_lineage_state_a_trusted_import_advances_in_place()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        BeaconStateFulu lineage = chain.AnchorState.Clone();
        PostStateCache states = new(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), chain.Spec, chain.AnchorRoot, lineage);
        states.Retain(chain.AnchorRoot, lineage.Clone());

        SlotProcessing.ProcessSlots(lineage, chain.AnchorState.Slot + 1, new EpochCache());

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(states.LineageState!.Slot, Is.EqualTo(chain.AnchorState.Slot + 1), "fixture: the lineage moved on in place");
        Assert.That(states.GetBlockState(chain.AnchorRoot)!.Slot, Is.EqualTo(chain.AnchorState.Slot));
    }
}
