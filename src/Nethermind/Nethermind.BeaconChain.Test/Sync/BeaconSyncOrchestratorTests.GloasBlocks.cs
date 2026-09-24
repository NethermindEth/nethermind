// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// <see cref="IBlockImporter"/> takes only Fulu blocks, so a Gloas block that reaches the orchestrator must be
/// dropped without penalizing the peer that served it, without parking it for a retry that can never succeed,
/// and without range sync downloading the rest of the Gloas chain every slot.
/// </summary>
public partial class BeaconSyncOrchestratorTests
{
    [Test]
    public async Task Gossip_gloas_block_is_dropped_before_the_gossip_checks_and_never_retried()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(AnchorSlot);
        harness.Importer.Known.Add(anchorRoot);
        BeaconSyncOrchestrator orchestrator = harness.Orchestrator;

        // Same slot and proposer as the Fulu block below; its parent is unknown, so a later drop would first queue it behind that parent.
        await orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(150, TestItem.KeccakB)), CancellationToken.None);
        long droppedOnArrival = orchestrator.DroppedGloasBlocks;
        await orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(150, anchorRoot)), CancellationToken.None);
        await orchestrator.ProcessSlotAsync(151, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(droppedOnArrival, Is.EqualTo(1), "dropped on arrival, not queued behind its unknown parent");
            Assert.That(orchestrator.DroppedGloasBlocks, Is.EqualTo(1), "a slot tick does not retry it");
            Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[150]), "only the Fulu block reaches the importer, so the Gloas one did not mark (slot, proposer) seen");
            Assert.That(harness.Pool.GetBestPeersCalls, Is.Zero, "no by-root backfill was started for it");
        }
    }

    [Test]
    public async Task Range_round_stops_after_the_first_gloas_block_and_drops_it_without_a_peer_penalty()
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] fuluBlocks) = TestChain.BuildLinkedChain(AnchorSlot, 101, 102);
        List<ForkedSignedBeaconBlock> chain = [.. fuluBlocks.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        Hash256 parentRoot = chain[^1].ComputeMessageRoot();
        for (ulong slot = 103; slot <= WallSlot; slot++)
        {
            ForkedSignedBeaconBlock gloas = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(slot, parentRoot));
            chain.Add(gloas);
            parentRoot = gloas.ComputeMessageRoot();
        }

        RangeSyncTests.StubPeer peer = new("peer", WallSlot, (startSlot, count) => [.. chain.Where(b => b.Slot >= startSlot && b.Slot < startSlot + count)]);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        BeaconSyncOrchestrator orchestrator = harness.Orchestrator;

        await orchestrator.FeedRangeSyncRoundAsync(CancellationToken.None);
        orchestrator.WorkWriter.Complete();
        await orchestrator.RunWorkerAsync(CancellationToken.None);
        await orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[101, 102]), "the Fulu blocks import");
            Assert.That(orchestrator.DroppedGloasBlocks, Is.EqualTo(1), "only the first Gloas block is fed, and a slot tick does not retry it");
            Assert.That(peer.Requests, Is.EqualTo(1), "the round ends at the first Gloas block rather than downloading to the wall clock");
            Assert.That(peer.Failures, Is.Zero, "serving a Gloas block is not a peer fault");
            Assert.That(orchestrator.SyncTip.Slot, Is.EqualTo(102UL), "the tip rests on the last imported block");
        }
    }
}
