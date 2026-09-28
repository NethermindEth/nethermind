// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>Gloas blocks through the orchestrator: routed to the importer, parked on an unverified parent payload, and re-driven by its envelope.</summary>
public partial class BeaconSyncOrchestratorTests
{
    [Test]
    public async Task Gossip_gloas_block_reaches_the_importer_as_a_gloas_block()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(AnchorSlot);
        harness.Importer.Known.Add(anchorRoot);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(150, anchorRoot));

        await harness.Orchestrator.ProcessGossipBlockAsync(block, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports.Select(static i => i.Root), Is.EqualTo((Hash256[])[block.ComputeMessageRoot()]));
            Assert.That(harness.Router.IsProposalSeen(150, block.ProposerIndex), Is.True, "an imported Gloas block marks its (slot, proposer) seen");
        }
    }

    /// <summary>Range sync feeds the whole Gloas chain instead of stopping at its first block and fetching it again every round.</summary>
    [Test]
    public async Task Range_round_imports_past_the_fork()
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

        await harness.Orchestrator.FeedRangeSyncRoundAsync(CancellationToken.None);
        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo(chain.Select(static b => b.Slot)));
            Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(WallSlot));
        }
    }

    /// <summary>
    /// A block parked on its parent's unverified payload is retried on the slot tick like any retriable result, and at
    /// once when that parent's envelope records the payload. An envelope that records nothing re-drives nothing.
    /// </summary>
    [TestCase(ExecutionPayloadEnvelopeImportResult.Valid, true)]
    [TestCase(ExecutionPayloadEnvelopeImportResult.Optimistic, true)]
    [TestCase(ExecutionPayloadEnvelopeImportResult.Invalid, false)]
    [TestCase(ExecutionPayloadEnvelopeImportResult.DataUnavailable, false)]
    public async Task Block_waiting_on_its_parents_payload_is_retried_and_re_driven_by_the_envelope(ExecutionPayloadEnvelopeImportResult envelopeResult, bool reDriven)
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(AnchorSlot);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(150, anchorRoot));
        Hash256 parentRoot = parent.ComputeMessageRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(151, parentRoot));
        harness.Importer.Known.UnionWith([anchorRoot, parentRoot]);
        harness.Importer.UnverifiedPayloads.Add(parentRoot);
        harness.Importer.EnvelopeResult = envelopeResult;
        BeaconSyncOrchestrator orchestrator = harness.Orchestrator;

        await orchestrator.ProcessGossipBlockAsync(child, CancellationToken.None);
        await orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        int attemptsBeforeEnvelope = harness.Importer.Imports.Count;
        await orchestrator.ImportEnvelopeAsync(EnvelopeFor(parentRoot, WallSlot), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attemptsBeforeEnvelope, Is.EqualTo(2), "the slot tick retries the parked block");
            Assert.That(harness.Router.IsProposalSeen(151, child.ProposerIndex), Is.True, "a parked block passed its proposer signature check, so a second block for its (slot, proposer) is a repeat");
            Assert.That(harness.Importer.Imports, Has.Count.EqualTo(reDriven ? 3 : 2), "the envelope re-drives the parked block only when it records the payload");
            Assert.That(harness.Importer.Known.Contains(child.ComputeMessageRoot()), Is.EqualTo(reDriven));
        }
    }

    /// <summary>
    /// A gossip block whose parent waits in the retry set on its own parent's payload is held for that parent, not dropped,
    /// and the parent is not fetched again when the node already holds it; the envelope then imports both in order.
    /// </summary>
    [Test]
    public async Task Gossip_child_of_a_parent_waiting_on_its_payload_imports_after_the_envelope([Values] bool parentAlreadyHeld)
    {
        ParkedParentScenario scenario = CreateParkedParentScenario();
        BeaconSyncOrchestrator orchestrator = scenario.Harness.Orchestrator;

        if (parentAlreadyHeld)
        {
            await orchestrator.ProcessGossipBlockAsync(scenario.Parent, CancellationToken.None);
        }

        await orchestrator.ProcessGossipBlockAsync(scenario.Child, CancellationToken.None);
        await orchestrator.ImportEnvelopeAsync(EnvelopeFor(scenario.FullRoot, WallSlot), CancellationToken.None);
        await orchestrator.ProcessGossipBlockAsync(scenario.Grandchild, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.Harness.Importer.Known, Does.Contain(scenario.Child.ComputeMessageRoot()), "the held child imports once its parent does");
            Assert.That(scenario.Harness.Importer.Known, Does.Contain(scenario.Grandchild.ComputeMessageRoot()), "a child of the imported block is not held for it");
            Assert.That(orchestrator.SyncTip, Is.EqualTo((scenario.Grandchild.ComputeMessageRoot(), scenario.Grandchild.Slot)), "a child of the imported block goes through the full import");
            Assert.That(scenario.Harness.Router.IsProposalSeen(scenario.Grandchild.Slot, scenario.Grandchild.ProposerIndex), Is.True);
            Assert.That(scenario.Peer.ReceivedCalls().Count(static c => c.GetMethodInfo().Name == nameof(IBeaconSyncPeer.RequestBlocksByRootAsync)), Is.EqualTo(parentAlreadyHeld ? 0 : 1), "a parent the node holds is not fetched");
            Assert.That(orchestrator.PendingGossipBlockCount, Is.Zero);
        }
    }

    /// <summary>
    /// A child held for a parked block, and the blocks held for that child, are released when the parked block leaves the
    /// retry set without importing, here by failing its retry or by falling behind finality; held for good, such blocks would fill the bounded queue.
    /// </summary>
    [Test]
    public async Task Child_held_for_a_parked_block_is_released_when_that_block_can_no_longer_import([Values] ParkedBlockFate fate)
    {
        ParkedParentScenario scenario = CreateParkedParentScenario();
        Harness harness = scenario.Harness;
        harness.Orchestrator.GossipStarted = true;
        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Parent, CancellationToken.None);
        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Child, CancellationToken.None);
        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Grandchild, CancellationToken.None);
        int heldBefore = harness.Orchestrator.PendingGossipBlockCount;

        switch (fate)
        {
            case ParkedBlockFate.FinalizedAway:
                harness.Importer.Head = CreateHead(TestItem.KeccakA, WallSlot, finalizedEpoch: Spec.GetEpoch(scenario.Parent.Slot) + 1);
                break;
            case ParkedBlockFate.RetryInvalid:
                harness.Importer.UnverifiedPayloads.Remove(scenario.FullRoot);
                harness.Importer.Forged.Add(scenario.Parent.ComputeMessageRoot());
                break;
            case ParkedBlockFate.RetryUnknownParent:
                harness.Importer.Known.Remove(scenario.FullRoot);
                break;
        }

        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        int heldAfterRelease = harness.Orchestrator.PendingGossipBlockCount;
        ForkedSignedBeaconBlock lateSibling = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(scenario.Grandchild.Slot + 1, scenario.Child.ComputeMessageRoot()));
        await harness.Orchestrator.ProcessGossipBlockAsync(lateSibling, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldBefore, Is.EqualTo(2));
            Assert.That(heldAfterRelease, Is.Zero);
            Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.Zero, "a released block no longer holds children of its own");
        }
    }

    public enum ParkedBlockFate
    {
        FinalizedAway,
        RetryInvalid,
        RetryUnknownParent,
    }

    /// <summary>
    /// A block whose parent is itself held for a parked block is held too, whether it arrives as that parent's child or
    /// needs the blocks between fetched first; the node never fetches a block it holds, and the envelope imports them all.
    /// </summary>
    [Test]
    public async Task Descendants_of_a_held_child_are_held_without_fetching_it_again([Values] bool parentUnknown)
    {
        ParkedParentScenario scenario = CreateParkedParentScenario();
        Harness harness = scenario.Harness;
        Hash256 childRoot = scenario.Child.ComputeMessageRoot();
        Hash256 grandchildRoot = scenario.Grandchild.ComputeMessageRoot();
        ForkedSignedBeaconBlock greatGrandchild = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(scenario.Grandchild.Slot + 1, grandchildRoot));
        scenario.Peer.RequestBlocksByRootAsync(Arg.Is<Hash256[]>(r => r[0] == childRoot), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([scenario.Child]));
        scenario.Peer.RequestBlocksByRootAsync(Arg.Is<Hash256[]>(r => r[0] == grandchildRoot), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([scenario.Grandchild]));
        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Parent, CancellationToken.None);
        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Child, CancellationToken.None);

        await harness.Orchestrator.ProcessGossipBlockAsync(parentUnknown ? greatGrandchild : scenario.Grandchild, CancellationToken.None);
        int heldBeforeEnvelope = harness.Orchestrator.PendingGossipBlockCount;
        await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(scenario.FullRoot, WallSlot), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldBeforeEnvelope, Is.EqualTo(parentUnknown ? 3 : 2));
            Assert.That(ByRootRequestsFor(scenario.Peer, childRoot), Is.Zero, "the held child is not fetched");
            Assert.That(ByRootRequestsFor(scenario.Peer, grandchildRoot), Is.EqualTo(parentUnknown ? 1 : 0));
            Assert.That(harness.Importer.Known, Does.Contain(grandchildRoot));
            Assert.That(harness.Importer.Known.Contains(greatGrandchild.ComputeMessageRoot()), Is.EqualTo(parentUnknown));
            Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.Zero);
        }
    }

    /// <summary>
    /// A block that answers UnknownParent without ever having been retried can still import once range sync delivers its
    /// parent, so the far-behind gossip blocks held for it stay held and import with it.
    /// </summary>
    [Test]
    public async Task Children_held_for_a_block_with_an_unknown_parent_import_with_it()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, WallSlot - 2, WallSlot - 1);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        await harness.Orchestrator.ProcessGossipBlockAsync(child, CancellationToken.None);

        BlockImportResult beforeAnchor = await harness.Orchestrator.ImportBlockAsync(parent, CancellationToken.None);
        int heldAfterUnknownParent = harness.Orchestrator.PendingGossipBlockCount;
        harness.Importer.Known.Add(anchorRoot);
        await harness.Orchestrator.ImportBlockAsync(parent, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeAnchor, Is.EqualTo(BlockImportResult.UnknownParent));
            Assert.That(heldAfterUnknownParent, Is.EqualTo(1));
            Assert.That(harness.Importer.Known, Does.Contain(child.ComputeMessageRoot()), "the held child imports with its parent");
            Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.Zero);
        }
    }

    /// <summary>
    /// A child of a parked block is held only once its proposer signature checks out; a forged one is neither held nor makes the
    /// node fetch, also while the node is far behind, where a block with an unknown parent is otherwise held unchecked.
    /// </summary>
    [Test]
    public async Task Forged_child_of_a_parked_block_is_not_held([Values] bool farBehind)
    {
        ParkedParentScenario scenario = CreateParkedParentScenario(farBehind);
        Harness harness = scenario.Harness;
        harness.Importer.Forged.Add(scenario.Child.ComputeMessageRoot());
        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Parent, CancellationToken.None);
        int callsBeforeChild = scenario.Peer.ReceivedCalls().Count();

        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Child, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.Zero);
            Assert.That(harness.Router.IsProposalSeen(scenario.Child.Slot, scenario.Child.ProposerIndex), Is.False, "a forged block does not take its (slot, proposer)");
            Assert.That(scenario.Peer.ReceivedCalls().Count(), Is.EqualTo(callsBeforeChild), "the forged child asks no peer for anything");
        }
    }

    /// <summary>
    /// A child is held only when the importer verified its proposer and deferred it; one the importer can no longer defer (its
    /// bounded deferral set forgot the parked parent) was never signature-checked, so it takes no queue place and no (slot, proposer).
    /// </summary>
    [Test]
    public async Task Child_of_a_parked_block_the_importer_no_longer_defers_is_not_held()
    {
        ParkedParentScenario scenario = CreateParkedParentScenario();
        Harness harness = scenario.Harness;
        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Parent, CancellationToken.None);
        harness.Importer.Known.Remove(scenario.FullRoot);
        Assert.That(harness.Importer.Import(scenario.Parent, scenario.Parent.ComputeMessageRoot(), verifySignatures: true), Is.EqualTo(BlockImportResult.UnknownParent), "fixture: the importer forgets the parked block");
        harness.Importer.Known.Add(scenario.FullRoot);

        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Child, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.Zero);
            Assert.That(harness.Router.IsProposalSeen(scenario.Child.Slot, scenario.Child.ProposerIndex), Is.False);
        }
    }

    /// <summary>
    /// specs/phase0/p2p-interface.md <c>beacon_block</c> ignores all but the first signed block for a (slot, proposer): a replay
    /// of a held child, or another block its proposer signed for that slot, must not take a second place in the bounded queue.
    /// </summary>
    [Test]
    public async Task Child_of_a_parked_block_is_held_once_per_slot_and_proposer([Values] bool equivocation)
    {
        ParkedParentScenario scenario = CreateParkedParentScenario();
        Harness harness = scenario.Harness;
        SignedBeaconBlockGloas equivocating = CreateMinimalGloasBlock(scenario.Child.Slot, scenario.Parent.ComputeMessageRoot());
        equivocating.Message!.Body!.Graffiti = TestItem.KeccakB;
        ForkedSignedBeaconBlock repeat = equivocation ? new ForkedSignedBeaconBlock.OfGloas(equivocating) : scenario.Child;
        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Parent, CancellationToken.None);
        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Child, CancellationToken.None);

        await harness.Orchestrator.ProcessGossipBlockAsync(repeat, CancellationToken.None);

        Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.EqualTo(1));
    }

    /// <summary>
    /// A fetched ancestor of a gossip block passes the same (slot, proposer) gate as a gossip block before it is held for a
    /// parked block, or one equivocating proposer could fill the bounded queue through forged children naming each of its blocks.
    /// </summary>
    [Test]
    public async Task Fetched_ancestor_of_a_parked_block_is_not_held_for_a_seen_slot_and_proposer()
    {
        ParkedParentScenario scenario = CreateParkedParentScenario();
        Harness harness = scenario.Harness;
        Hash256 childRoot = scenario.Child.ComputeMessageRoot();
        scenario.Peer.RequestBlocksByRootAsync(Arg.Is<Hash256[]>(r => r[0] == childRoot), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([scenario.Child]));
        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Parent, CancellationToken.None);
        harness.Router.MarkProposalSeen(scenario.Child.Slot, scenario.Child.ProposerIndex);

        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Grandchild, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ByRootRequestsFor(scenario.Peer, childRoot), Is.EqualTo(1), "fixture: the ancestor is fetched");
            Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.Zero);
        }
    }

    /// <summary>
    /// A child of a parked block that the full queue cannot take is neither deferred by the importer nor marked seen, so a
    /// later copy can still be held once there is room; marked seen, the valid block would be refused from gossip for good.
    /// </summary>
    [Test]
    public async Task Child_of_a_parked_block_the_full_queue_cannot_take_is_not_marked_seen()
    {
        const int PendingQueueCapacity = 128;
        ParkedParentScenario scenario = CreateParkedParentScenario(farBehind: true);
        Harness harness = scenario.Harness;
        Hash256 childRoot = scenario.Child.ComputeMessageRoot();
        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Parent, CancellationToken.None);
        for (int i = 0; i < PendingQueueCapacity; i++)
        {
            ForkedSignedBeaconBlock filler = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(scenario.Grandchild.Slot + 1 + (ulong)i, TestItem.Keccaks[i]));
            await harness.Orchestrator.ProcessGossipBlockAsync(filler, CancellationToken.None);
        }

        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Child, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.EqualTo(PendingQueueCapacity), "fixture: the queue is full");
            Assert.That(harness.Router.IsProposalSeen(scenario.Child.Slot, scenario.Child.ProposerIndex), Is.False);
            Assert.That(harness.Importer.Imports.Any(i => i.Root == childRoot), Is.False, "the importer records no deferral for a block the queue cannot take");
        }
    }

    /// <summary>
    /// A Fulu block in the retry set waits on its data, not on a payload, so its gossip child takes the missing-parent path:
    /// once the parent's data is held, the parent fetched for the child imports, and the child with it.
    /// </summary>
    [Test]
    public async Task Child_of_a_fulu_block_waiting_on_its_data_imports_with_its_fetched_parent()
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearWallSlot, NearWallSlot + 1, NearWallSlot + 2);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        Hash256 parentRoot = parent.ComputeMessageRoot();
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([parent]));
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Unavailable.Add(parentRoot);
        BlockImportResult parked = await harness.Orchestrator.ImportBlockAsync(parent, CancellationToken.None);
        harness.Importer.Unavailable.Remove(parentRoot);

        await harness.Orchestrator.ProcessGossipBlockAsync(child, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parked, Is.EqualTo(BlockImportResult.DataUnavailable), "fixture: the parent waits in the retry set");
            Assert.That(harness.Importer.Known, Does.Contain(parentRoot));
            Assert.That(harness.Importer.Known, Does.Contain(child.ComputeMessageRoot()));
        }
    }

    /// <summary>A far-behind gossip block held for an unknown parent is released when the full retry set refuses that parent.</summary>
    [Test]
    public async Task Child_held_for_a_block_the_full_retry_set_refuses_is_released()
    {
        const int RetrySetCapacity = 128;
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, WallSlot - 2, WallSlot - 1);
        harness.Importer.Known.Add(anchorRoot);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[1]), CancellationToken.None);
        int heldBefore = harness.Orchestrator.PendingGossipBlockCount;

        for (int i = 0; i < RetrySetCapacity; i++)
        {
            ForkedSignedBeaconBlock waiting = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(WallSlot + 1 + (ulong)i, anchorRoot));
            harness.Importer.Unavailable.Add(waiting.ComputeMessageRoot());
            await harness.Orchestrator.ImportBlockAsync(waiting, CancellationToken.None);
        }

        harness.Importer.Unavailable.Add(parent.ComputeMessageRoot());
        await harness.Orchestrator.ImportBlockAsync(parent, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldBefore, Is.EqualTo(1));
            Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.Zero);
        }
    }

    private sealed record ParkedParentScenario(Harness Harness, IBeaconSyncPeer Peer, Hash256 FullRoot, ForkedSignedBeaconBlock Parent, ForkedSignedBeaconBlock Child, ForkedSignedBeaconBlock Grandchild);

    private static int ByRootRequestsFor(IBeaconSyncPeer peer, Hash256 root) =>
        peer.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBeaconSyncPeer.RequestBlocksByRootAsync) && ((Hash256[])c.GetArguments()[0]!)[0] == root);

    /// <summary>
    /// A known full block whose payload is unverified, the unknown parent that builds on it, that parent's child and grandchild; a peer
    /// serves the parent by root. The node's sync tip is the anchor, near the wall clock or, <paramref name="farBehind"/>, more than a backfill away.
    /// </summary>
    private static ParkedParentScenario CreateParkedParentScenario(bool farBehind = false)
    {
        ulong anchorSlot = farBehind ? WallSlot - 40 : WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(anchorSlot);
        ForkedSignedBeaconBlock full = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(anchorSlot + 1, anchorRoot));
        Hash256 fullRoot = full.ComputeMessageRoot();
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(anchorSlot + 2, fullRoot));
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(anchorSlot + 3, parent.ComputeMessageRoot()));
        ForkedSignedBeaconBlock grandchild = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(anchorSlot + 4, child.ComputeMessageRoot()));
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([parent]));
        Harness harness = CreateHarness(anchorSlot: anchorSlot, peers: [peer]);
        harness.Importer.Known.UnionWith([anchorRoot, fullRoot]);
        harness.Importer.UnverifiedPayloads.Add(fullRoot);
        return new ParkedParentScenario(harness, peer, fullRoot, parent, child, grandchild);
    }

    /// <summary>A restart replays stored Gloas blocks; the Fulu-only store read would throw on the first of them and stop the node.</summary>
    [Test]
    public async Task Replay_reads_stored_gloas_blocks()
    {
        BeaconChainSpec storeSpec = Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures.SyntheticSpec(gloasForkEpoch: 4);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), storeSpec);
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] fulu) = TestChain.BuildLinkedChain(AnchorSlot, 101);
        TestChain.Persist(store, anchor, anchorRoot, fulu);
        ForkedSignedBeaconBlock gloas = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(4 * storeSpec.SlotsPerEpoch, SszRoots.HashTreeRoot(fulu[0].Message!)));
        store.PutForkedBlock(gloas.ComputeMessageRoot(), gloas);
        store.SetCanonicalRoot(gloas.Slot, gloas.ComputeMessageRoot());

        Harness harness = CreateHarness(store: store);
        harness.Importer.Known.Add(anchorRoot);
        await harness.Orchestrator.ReplayStoredBlocksAsync(CancellationToken.None);

        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[101, gloas.Slot]));
    }

    /// <summary>An envelope that brings in no parked block still moves the execution head from the bid's parent hash to its block hash.</summary>
    [TestCase(ExecutionPayloadEnvelopeImportResult.Valid, 1)]
    [TestCase(ExecutionPayloadEnvelopeImportResult.Invalid, 0)]
    public async Task Envelope_that_re_drives_no_block_still_runs_a_head_step(ExecutionPayloadEnvelopeImportResult envelopeResult, int expectedFcus)
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeResult = envelopeResult;
        BeaconSyncOrchestrator orchestrator = harness.Orchestrator;

        await orchestrator.ImportEnvelopeAsync(EnvelopeFor(TestItem.KeccakA, WallSlot), CancellationToken.None);
        orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipAggregateItem(new SignedAggregateAndProof()));
        orchestrator.WorkWriter.Complete();
        await orchestrator.RunWorkerAsync(CancellationToken.None);

        Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(expectedFcus));
    }

    /// <summary>
    /// A Fulu chain crossing into Gloas through the orchestrator and the real importer: a Fulu block past the anchor
    /// imports, then the first Gloas block on it, its full child waits for that block's envelope, and the envelope's
    /// import brings the child in.
    /// </summary>
    [Test]
    public async Task Fulu_chain_crosses_into_gloas_and_a_full_child_imports_once_its_parents_envelope_does()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.FuluBlock lastFulu = chain.NextFulu(31);
        SignedGloasChain.Block first = chain.NextOnFulu(lastFulu, 32, full: false, 0xC1);
        SignedGloasChain.Block child = chain.Next(first, 33, full: true, 0xC2);
        (BeaconSyncOrchestrator orchestrator, BlockImporter importer, SignedGloasChain.EnvelopeEngine engine) = CreateGloasOrchestrator(chain);

        await orchestrator.ProcessGossipBlockAsync(lastFulu.Forked, CancellationToken.None);
        await orchestrator.ProcessGossipBlockAsync(first.Forked, CancellationToken.None);
        await orchestrator.ProcessGossipBlockAsync(child.Forked, CancellationToken.None);
        bool childKnownBeforeEnvelope = importer.IsKnown(child.Root);
        ExecutionPayloadEnvelopeImportResult? envelope = await orchestrator.ImportEnvelopeAsync(first.Envelope, CancellationToken.None);
        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importer.IsKnown(lastFulu.Root), Is.True, "the Fulu block past the anchor imports");
            Assert.That(importer.IsKnown(first.Root), Is.True, "the first Gloas block imports across the fork");
            Assert.That(childKnownBeforeEnvelope, Is.False, "the full child waits for its parent's payload");
            Assert.That(envelope, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
            Assert.That(importer.IsKnown(child.Root), Is.True, "the envelope re-drives the parked child");
            Assert.That(orchestrator.SyncTip, Is.EqualTo((child.Root, 33UL)));
            Assert.That(importer.ComputeHead().HeadRoot, Is.EqualTo(child.Root), "the re-driven child is the head");
            Assert.That(engine.FcuCalls[^1].Head, Is.EqualTo(child.Bid.ParentBlockHash), "the child's own payload has not arrived, so the fcU head is its bid's parent_block_hash");
        }
    }

    /// <summary>
    /// The retry set is bounded, so blocks parked there must be ones their proposer signed. Forged children of a full
    /// parent, sent before its envelope to fill that set, must not stop the real child from importing once it arrives.
    /// </summary>
    [Test]
    public async Task Forged_children_of_a_full_parent_do_not_crowd_out_the_real_one()
    {
        const int RetrySetCapacity = 128;
        SignedGloasChain chain = new();
        SignedGloasChain.Block first = chain.Next(null, 32, full: false, 0xC1);
        SignedGloasChain.Block child = chain.Next(first, 33, full: true, 0xC2);
        (BeaconSyncOrchestrator orchestrator, BlockImporter importer, _) = CreateGloasOrchestrator(chain);
        await orchestrator.ProcessGossipBlockAsync(first.Forked, CancellationToken.None);

        byte[] childSsz = SignedBeaconBlockCodec.Encode(child.Forked, chain.Spec);
        for (int i = 0; i < RetrySetCapacity; i++)
        {
            ForkedSignedBeaconBlock.OfGloas forged = (ForkedSignedBeaconBlock.OfGloas)SignedBeaconBlockCodec.Decode(childSsz, chain.Spec);
            forged.Block.Message!.Body!.Graffiti = Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures.Hash((byte)(0x80 + i));
            await orchestrator.ProcessGossipBlockAsync(forged, CancellationToken.None);
        }

        await orchestrator.ProcessGossipBlockAsync(child.Forked, CancellationToken.None);
        await orchestrator.ImportEnvelopeAsync(first.Envelope, CancellationToken.None);

        Assert.That(importer.IsKnown(child.Root), Is.True);
    }

    /// <summary>
    /// The queue of blocks held for a parked parent is bounded too: forged children of the parked block, sent to fill it
    /// before the real child, must not stop that child from importing once the envelope arrives.
    /// </summary>
    [Test]
    public async Task Forged_children_of_a_parked_block_do_not_crowd_out_the_real_one()
    {
        const int PendingQueueCapacity = 128;
        SignedGloasChain chain = new();
        SignedGloasChain.Block first = chain.Next(null, 32, full: false, 0xC1);
        SignedGloasChain.Block parked = chain.Next(first, 33, full: true, 0xC2);
        SignedGloasChain.Block child = chain.Next(parked, 34, full: false, 0xC3);
        (BeaconSyncOrchestrator orchestrator, BlockImporter importer, _) = CreateGloasOrchestrator(chain);
        await orchestrator.ProcessGossipBlockAsync(first.Forked, CancellationToken.None);
        await orchestrator.ProcessGossipBlockAsync(parked.Forked, CancellationToken.None);

        byte[] childSsz = SignedBeaconBlockCodec.Encode(child.Forked, chain.Spec);
        for (int i = 0; i < PendingQueueCapacity; i++)
        {
            ForkedSignedBeaconBlock.OfGloas forged = (ForkedSignedBeaconBlock.OfGloas)SignedBeaconBlockCodec.Decode(childSsz, chain.Spec);
            forged.Block.Message!.Body!.Graffiti = Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures.Hash((byte)(0x80 + i));
            await orchestrator.ProcessGossipBlockAsync(forged, CancellationToken.None);
        }

        await orchestrator.ProcessGossipBlockAsync(child.Forked, CancellationToken.None);
        int heldBeforeEnvelope = orchestrator.PendingGossipBlockCount;
        await orchestrator.ImportEnvelopeAsync(first.Envelope, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldBeforeEnvelope, Is.EqualTo(1), "only the signed child is held");
            Assert.That(importer.IsKnown(parked.Root), Is.True);
            Assert.That(importer.IsKnown(child.Root), Is.True, "the envelope imports the parked block, which releases its child");
        }
    }

    /// <summary>An orchestrator on the real importer over <paramref name="chain"/>, its wall clock inside slot 34.</summary>
    private static (BeaconSyncOrchestrator Orchestrator, BlockImporter Importer, SignedGloasChain.EnvelopeEngine Engine) CreateGloasOrchestrator(SignedGloasChain chain)
    {
        BeaconChainSpec spec = chain.Spec;
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(spec.GenesisTime + 34 * spec.SecondsPerSlot).AddSeconds(6));
        SlotClock slotClock = new(spec, timestamper);
        BeaconChainStore store = chain.CreateStore();
        SignedGloasChain.EnvelopeEngine engine = new();
        StubPool pool = new([]);
        BlockImporter importer = chain.CreateImporter(engine, store: store);
        BeaconSyncOrchestrator orchestrator = new(
            new BeaconChainConfig(),
            spec,
            store,
            new ScriptedFactory(importer),
            engine,
            pool,
            new RangeSync(pool, LimboLogs.Instance, new DataColumnSidecarPool(), spec, RangeSyncTests.ClockAtGenesis(spec)),
            slotClock,
            new GossipRouter(spec, slotClock, LimboLogs.Instance),
            new BeaconChainStatusHolder(spec, timestamper),
            LimboLogs.Instance);
        orchestrator.Initialize(importer, chain.AnchorBlock, chain.AnchorRoot);
        orchestrator.GossipStarted = true;
        return (orchestrator, importer, engine);
    }
}
