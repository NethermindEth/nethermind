// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>Near the head, an unknown-parent gossip block is fetched by root before its signature can be checked, so the fetches it buys are bounded.</summary>
public partial class BeaconSyncOrchestratorTests
{
    private const ulong NearHeadAnchorSlot = WallSlot - 5;

    [TestCase(1, 1)]
    [TestCase(BeaconSyncOrchestrator.MaxBackfillsPerSlot, BeaconSyncOrchestrator.MaxBackfillsPerSlot)]
    [TestCase(40, BeaconSyncOrchestrator.MaxBackfillsPerSlot)]
    [TestCase(200, BeaconSyncOrchestrator.MaxBackfillsPerSlot)]
    public async Task Forged_unknown_parent_blocks_buy_at_most_the_per_slot_backfill_budget(int forged, int expectedFetches)
    {
        (Harness harness, IBeaconSyncPeer peer) = CreateBackfillHarness();

        // One slot and proposer for all, each naming its own parent: only the per-slot budget bounds them.
        for (int i = 0; i < forged; i++)
        {
            await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(UnknownParentBlock(WallSlot, i), CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ByRootRequests(peer), Is.EqualTo(expectedFetches));
            Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.EqualTo(Math.Min(forged, BeaconSyncOrchestrator.MaxHeldRefusedBackfills)),
                "unknown-parent blocks hold at most their own share of the queue shared with payload-held blocks");
        }
    }

    // A fetched parent waiting for its data is not fetched again; the children naming it wait with it.
    [Test]
    public async Task Blocks_naming_one_fetched_parent_buy_one_backfill()
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 3);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        (Harness harness, IBeaconSyncPeer peer) = CreateBackfillHarness(parent);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Unavailable.Add(parent.ComputeMessageRoot());

        for (int i = 0; i < 3; i++)
        {
            await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(ChildOf(parent, WallSlot - (ulong)i), CancellationToken.None);
        }

        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(UnknownParentBlock(WallSlot, seed: 1), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ByRootRequests(peer), Is.EqualTo(2), "one for the repeated parent, one for another parent");
            Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.EqualTo(3), "the later children of the waiting parent are held, and so is the block whose parent no peer returned");
        }
    }

    // A parent no peer returned may be fetched again, but each fetch still spends the slot's budget.
    [Test]
    public async Task Parent_no_peer_returned_is_fetched_again_within_the_slot_budget()
    {
        (Harness harness, IBeaconSyncPeer peer) = CreateBackfillHarness();

        for (int i = 0; i < BeaconSyncOrchestrator.MaxBackfillsPerSlot + 2; i++)
        {
            await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(UnknownParentBlock(WallSlot - (ulong)i, seed: 0), CancellationToken.None);
        }

        Assert.That(ByRootRequests(peer), Is.EqualTo(BeaconSyncOrchestrator.MaxBackfillsPerSlot));
    }

    // Forged blocks spend the slot's budget first; the real child waits, evicting the oldest held block, and imports once its parent arrives by gossip.
    [TestCase(0, TestName = "refused child held while the hold has room")]
    [TestCase(BeaconSyncOrchestrator.MaxHeldRefusedBackfills, TestName = "refused child held in place of the oldest held forged block")]
    public async Task Block_whose_backfill_is_refused_imports_when_its_parent_arrives(int forgedHeld)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        (Harness harness, IBeaconSyncPeer peer) = CreateBackfillHarness();
        harness.Importer.Known.Add(anchorRoot);
        for (int i = 0; i < BeaconSyncOrchestrator.MaxBackfillsPerSlot + forgedHeld; i++)
        {
            await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(UnknownParentBlock(WallSlot, i), CancellationToken.None);
        }

        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(child, CancellationToken.None);
        int fetchesBeforeParent = ByRootRequests(peer);
        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(parent, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fetchesBeforeParent, Is.EqualTo(BeaconSyncOrchestrator.MaxBackfillsPerSlot), "fixture: the forged blocks spent the slot's budget");
            Assert.That(harness.Importer.Known, Does.Contain(parent.ComputeMessageRoot()).And.Contain(child.ComputeMessageRoot()));
            Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.EqualTo(Math.Min(BeaconSyncOrchestrator.MaxBackfillsPerSlot + forgedHeld, BeaconSyncOrchestrator.MaxHeldRefusedBackfills - 1)), "the parent's import drained the held child");
        }
    }

    // The spec marks a (slot, proposer) seen only once its signature verifies, so an unsigned copy must not take the real block's backfill.
    [Test]
    public async Task Unsigned_block_naming_the_real_proposer_does_not_take_the_real_block_backfill()
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        ForkedSignedBeaconBlock forged = UnknownParentBlock(child.Slot, seed: 0);
        (Harness harness, IBeaconSyncPeer _) = CreateBackfillHarness(parent);
        harness.Importer.Known.Add(anchorRoot);

        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(forged, CancellationToken.None);
        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(child, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((forged.Slot, forged.ProposerIndex), Is.EqualTo((child.Slot, child.ProposerIndex)), "fixture: same slot and proposer");
            Assert.That(harness.Importer.Known, Does.Contain(child.ComputeMessageRoot()));
        }
    }

    [Test]
    public async Task Backfill_budget_is_renewed_each_slot()
    {
        (Harness harness, IBeaconSyncPeer peer) = CreateBackfillHarness();
        for (int i = 0; i < 2 * BeaconSyncOrchestrator.MaxBackfillsPerSlot; i++)
        {
            await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(UnknownParentBlock(WallSlot - (ulong)i, i), CancellationToken.None);
        }

        harness.Timestamper.Set(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + (WallSlot + 1) * Spec.SecondsPerSlot));
        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(UnknownParentBlock(WallSlot + 1, 100), CancellationToken.None);

        Assert.That(ByRootRequests(peer), Is.EqualTo(BeaconSyncOrchestrator.MaxBackfillsPerSlot + 1));
    }

    [Test]
    public async Task Block_whose_backfill_is_refused_imports_with_a_later_block_that_fetches_it()
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        (Harness harness, IBeaconSyncPeer peer) = CreateBackfillHarness(parent);
        harness.Importer.Known.Add(anchorRoot);
        for (int i = 0; i < BeaconSyncOrchestrator.MaxBackfillsPerSlot; i++)
        {
            await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(UnknownParentBlock(WallSlot - 2 - (ulong)i, i), CancellationToken.None);
        }

        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(child, CancellationToken.None);
        bool childImportedInBudgetSlot = harness.Importer.Known.Contains(child.ComputeMessageRoot());
        harness.Timestamper.Set(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + (WallSlot + 1) * Spec.SecondsPerSlot));
        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(child, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(childImportedInBudgetSlot, Is.False, "fixture: the forged blocks spent the slot's budget");
            Assert.That(harness.Importer.Known, Does.Contain(parent.ComputeMessageRoot()).And.Contain(child.ComputeMessageRoot()));
            Assert.That(ByRootRequests(peer), Is.EqualTo(BeaconSyncOrchestrator.MaxBackfillsPerSlot + 1));
        }
    }

    // A parent held behind a deeper ancestor that waits for the regeneration budget is not fetched again for another child in the slot.
    [Test]
    public async Task Parent_held_behind_a_budget_deferred_ancestor_buys_no_second_backfill()
    {
        DeferredAncestorWalk walk = await WalkToBudgetDeferredAncestorAsync();
        ForkedSignedBeaconBlock sibling = ChildOf(walk.Blocks[1], WallSlot - 1);

        await walk.Harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(sibling, CancellationToken.None);
        walk.Harness.Importer.RegenerationRefused.Remove(walk.Blocks[0].ComputeMessageRoot());
        await walk.Harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ByRootRequestsFor(walk.Peer, walk.Blocks[1].ComputeMessageRoot()), Is.EqualTo(1), "the held parent is not fetched again");
            Assert.That(walk.Harness.Importer.Known, Does.Contain(walk.Blocks[2].ComputeMessageRoot()).And.Contain(sibling.ComputeMessageRoot()), "both children import behind the deferred ancestor");
        }
    }

    // Once the cap of refused backfills evicts the held parent nothing holds it, so later children in the same slot may fetch it, each after the last fetch failed.
    [Test]
    public async Task Evicted_parent_of_a_budget_deferred_ancestor_may_be_fetched_again_in_the_slot()
    {
        DeferredAncestorWalk walk = await WalkToBudgetDeferredAncestorAsync();
        Hash256 heldParentRoot = walk.Blocks[1].ComputeMessageRoot();
        // Children of a parent being fetched are held without spending the slot's budget, and evict the oldest held blocks.
        for (int i = 0; i <= BeaconSyncOrchestrator.MaxHeldRefusedBackfills; i++)
        {
            await walk.Harness.Orchestrator.ProcessGossipBlockAsync(UnknownParentBlock(WallSlot + (ulong)i, seed: 0), CancellationToken.None);
        }

        walk.OtherParentFetch.SetResult([]);
        await walk.Harness.Orchestrator.SettleWithinAsync(maxPasses: 10, CancellationToken.None);
        walk.Withheld.Add(heldParentRoot);

        await walk.Harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(ChildOf(walk.Blocks[1], WallSlot - 1), CancellationToken.None);
        await walk.Harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(ChildOf(walk.Blocks[1], WallSlot), CancellationToken.None);

        Assert.That(ByRootRequestsFor(walk.Peer, heldParentRoot), Is.EqualTo(3), "each later child fetches the evicted parent");
    }

    /// <summary>
    /// A held parent that leaves the hold without importing, because it is invalid or its deferred ancestor is, no longer holds
    /// its backfill: a later child in the same slot fetches it again.
    /// </summary>
    [Test]
    public async Task Held_parent_that_fails_behind_a_budget_deferred_ancestor_may_be_fetched_again_in_the_slot([Values] bool ancestorInvalid)
    {
        DeferredAncestorWalk walk = await WalkToBudgetDeferredAncestorAsync();
        Hash256 deferredRoot = walk.Blocks[0].ComputeMessageRoot();
        Hash256 heldParentRoot = walk.Blocks[1].ComputeMessageRoot();
        walk.Harness.Importer.RegenerationRefused.Remove(deferredRoot);
        walk.Harness.Importer.Forged.Add(ancestorInvalid ? deferredRoot : heldParentRoot);

        await walk.Harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        bool heldParentFailed = !walk.Harness.Importer.Known.Contains(heldParentRoot) && walk.Harness.Orchestrator.PendingGossipBlockCount == 0;
        walk.Withheld.Add(heldParentRoot);
        await walk.Harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(ChildOf(walk.Blocks[1], WallSlot - 1), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldParentFailed, Is.True, "fixture: the held parent left the hold without importing");
            Assert.That(ByRootRequestsFor(walk.Peer, heldParentRoot), Is.EqualTo(2));
        }
    }

    private sealed record DeferredAncestorWalk(Harness Harness, IBeaconSyncPeer Peer, ForkedSignedBeaconBlock[] Blocks, HashSet<Hash256> Withheld, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>> OtherParentFetch);

    /// <summary>
    /// Walks the gossip block <c>Blocks[2]</c> to its fetched ancestor <c>Blocks[0]</c>, which waits for the regeneration budget,
    /// so <c>Blocks[1]</c> and <c>Blocks[2]</c> are held behind it. The peer serves the chain by root except the withheld roots,
    /// and leaves any other root waiting on <c>OtherParentFetch</c>.
    /// </summary>
    private static async Task<DeferredAncestorWalk> WalkToBudgetDeferredAncestorAsync()
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 4, WallSlot - 3, WallSlot - 2);
        ForkedSignedBeaconBlock[] blocks = [.. chain.Select(static b => (ForkedSignedBeaconBlock)new ForkedSignedBeaconBlock.OfFulu(b))];
        Dictionary<Hash256, ForkedSignedBeaconBlock> byRoot = blocks.ToDictionary(static b => b.ComputeMessageRoot());
        HashSet<Hash256> withheld = [];
        TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>> otherParentFetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Hash256 root = ((Hash256[])call[0])[0];
            return !byRoot.TryGetValue(root, out ForkedSignedBeaconBlock? served) ? otherParentFetch.Task
                : Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>(withheld.Contains(root) ? [] : [served]);
        });
        Harness harness = CreateHarness(anchorSlot: NearHeadAnchorSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.RegenerationRefused.Add(blocks[0].ComputeMessageRoot());

        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(blocks[2], CancellationToken.None);
        Assert.That(harness.Orchestrator.PendingRetryBlockCount, Is.EqualTo(1), "fixture: the fetched ancestor waits for the regeneration budget");
        return new DeferredAncestorWalk(harness, peer, blocks, withheld, otherParentFetch);
    }

    /// <summary>A node near the head with one peer; it serves <paramref name="served"/> by root, or nothing.</summary>
    private static (Harness Harness, IBeaconSyncPeer Peer) CreateBackfillHarness(ForkedSignedBeaconBlock? served = null)
    {
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>(served is null ? [] : [served]));
        return (CreateHarness(anchorSlot: NearHeadAnchorSlot, peers: [peer]), peer);
    }

    private static ForkedSignedBeaconBlock ChildOf(ForkedSignedBeaconBlock parent, ulong slot) =>
        new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(slot, parent.ComputeMessageRoot()));

    private static ForkedSignedBeaconBlock UnknownParentBlock(ulong slot, int seed) =>
        new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(slot, Keccak.Compute($"unknown parent {seed}")));

    private static int ByRootRequests(IBeaconSyncPeer peer) =>
        peer.ReceivedCalls().Count(static c => c.GetMethodInfo().Name == nameof(IBeaconSyncPeer.RequestBlocksByRootAsync));
}
