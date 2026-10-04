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

/// <summary>A by-root ancestor fetch can wait out several peers' timeouts, so it runs off the worker, which keeps importing and ticking meanwhile.</summary>
public partial class BeaconSyncOrchestratorTests
{
    [Test]
    public async Task Head_imports_and_slot_ticks_proceed_while_an_ancestor_fetch_waits_and_the_chain_imports_in_order_once_it_arrives()
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 3, WallSlot - 2, WallSlot - 1);
        ForkedSignedBeaconBlock[] blocks = [.. chain.Select(static b => (ForkedSignedBeaconBlock)new ForkedSignedBeaconBlock.OfFulu(b))];
        (Harness harness, IBeaconSyncPeer peer, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches) = CreateWaitingByRootHarness();
        harness.Importer.Known.Add(anchorRoot);
        ForkedSignedBeaconBlock head = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(WallSlot, anchorRoot));
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        Task gossip = harness.Orchestrator.ProcessGossipBlockAsync(blocks[2], cts.Token);
        bool gossipReturnedWhileFetchWaits = gossip.IsCompleted;
        await harness.Orchestrator.WorkWriter.WriteAsync(new BeaconSyncOrchestrator.GossipBlockItem(head), cts.Token);
        await harness.Orchestrator.WorkWriter.WriteAsync(new BeaconSyncOrchestrator.SlotTickItem(WallSlot), cts.Token);
        await harness.Orchestrator.ProcessQueuedAsync(cts.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gossipReturnedWhileFetchWaits, Is.True, "the worker does not wait for the by-root fetch");
            Assert.That(harness.Importer.Known, Does.Contain(head.ComputeMessageRoot()), "a head block imports while the fetch waits");
            Assert.That(harness.Importer.Ticks, Does.Contain(WallSlot), "a slot tick runs while the fetch waits");
            Assert.That(harness.Orchestrator.AncestorFetchesInFlight, Is.EqualTo(1));
        }

        fetches[blocks[1].ComputeMessageRoot()].SetResult([blocks[1]]);
        await WaitForFetchAsync(fetches, blocks[0].ComputeMessageRoot(), harness, cts.Token);
        fetches[blocks[0].ComputeMessageRoot()].SetResult([blocks[0]]);
        await harness.Orchestrator.SettleWithinAsync(maxPasses: 10, cts.Token);
        await gossip;

        Hash256[] chainRoots = [.. blocks.Select(static b => b.ComputeMessageRoot())];
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Imports.Select(static i => i.Root).Where(chainRoots.Contains), Is.EqualTo(chainRoots), "the fetched ancestors import oldest first, then the held gossip block");
        Assert.That(ByRootRequests(peer), Is.EqualTo(2));
        Assert.That(harness.Orchestrator.AncestorFetchesInFlight, Is.Zero);
    }

    // Blocks whose walks reach one unknown root share one request for it, also once the slot's budget has been renewed, and
    // also once the walk that fetched it waits on a deeper ancestor.
    [Test]
    public async Task An_ancestor_being_fetched_is_not_requested_again([Values] bool deeperAncestorFetched)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 3, WallSlot - 2);
        ForkedSignedBeaconBlock grandparent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        (Harness harness, IBeaconSyncPeer peer, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches) = CreateWaitingByRootHarness();
        harness.Importer.Known.Add(anchorRoot);
        if (!deeperAncestorFetched)
        {
            await harness.Orchestrator.ImportBlockAsync(grandparent, CancellationToken.None);
        }

        ForkedSignedBeaconBlock first = ChildOf(parent, WallSlot);
        ForkedSignedBeaconBlock second = ChildOf(parent, WallSlot + 1);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        await harness.Orchestrator.ProcessGossipBlockAsync(first, cts.Token);
        if (deeperAncestorFetched)
        {
            fetches[parent.ComputeMessageRoot()].SetResult([parent]);
            await WaitForFetchAsync(fetches, grandparent.ComputeMessageRoot(), harness, cts.Token);
        }

        SetWallSlot(harness, WallSlot + 1);
        await harness.Orchestrator.ProcessGossipBlockAsync(second, cts.Token);
        int requestsWhileWaiting = ByRootRequests(peer);
        ForkedSignedBeaconBlock fetched = deeperAncestorFetched ? grandparent : parent;
        fetches[fetched.ComputeMessageRoot()].SetResult([fetched]);
        CompleteOutstandingFetches(fetches);
        await harness.Orchestrator.SettleWithinAsync(maxPasses: 10, cts.Token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(requestsWhileWaiting, Is.EqualTo(deeperAncestorFetched ? 2 : 1));
        Assert.That(harness.Importer.Known, Does.Contain(first.ComputeMessageRoot()).And.Contain(second.ComputeMessageRoot()));
    }

    [Test]
    public async Task Ancestor_fetches_in_flight_stay_within_their_bound()
    {
        (Harness harness, IBeaconSyncPeer peer, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> _) = CreateWaitingByRootHarness();
        const int Bound = BeaconSyncOrchestrator.MaxConcurrentAncestorFetches;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        // Spread over slots so the per-slot budget never refuses one.
        for (int i = 0; i <= Bound; i++)
        {
            ulong slot = WallSlot + (ulong)(i / BeaconSyncOrchestrator.MaxBackfillsPerSlot);
            SetWallSlot(harness, slot);
            await harness.Orchestrator.ProcessGossipBlockAsync(UnknownParentBlock(slot, i), cts.Token);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(ByRootRequests(peer), Is.EqualTo(Bound));
        Assert.That(harness.Orchestrator.AncestorFetchesInFlight, Is.EqualTo(Bound));
        Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.EqualTo(1), "the block past the bound is held for its parent");
    }

    // A fetch can outlast the ancestor's arrival by another route; its block then imports at once, though no peer returned the
    // ancestor, while the fetch keeps its place in the bound until it ends. An ancestor that arrived but waits for its data
    // holds the block for its retry, whatever the fetch returns.
    [Test]
    public async Task Gossip_block_imports_when_its_parent_arrives_elsewhere_while_the_fetch_waits([Values] bool parentWaitsForData)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        Hash256 parentRoot = parent.ComputeMessageRoot();
        (Harness harness, IBeaconSyncPeer _, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches) = CreateWaitingByRootHarness();
        harness.Importer.Known.Add(anchorRoot);
        if (parentWaitsForData)
        {
            harness.Importer.Unavailable.Add(parentRoot);
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        await harness.Orchestrator.ProcessGossipBlockAsync(child, cts.Token);
        await harness.Orchestrator.ImportBlockAsync(parent, cts.Token);
        bool importedBeforeFetchEnded = harness.Importer.Known.Contains(child.ComputeMessageRoot());
        int fetchesWhileWaiting = harness.Orchestrator.AncestorFetchesInFlight;
        fetches[parentRoot].SetResult(parentWaitsForData ? [parent] : []);
        await harness.Orchestrator.SettleWithinAsync(maxPasses: 10, cts.Token);
        int heldAfterFetch = harness.Orchestrator.PendingGossipBlockCount;
        harness.Importer.Unavailable.Remove(parentRoot);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, cts.Token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(importedBeforeFetchEnded, Is.EqualTo(!parentWaitsForData), "the block does not wait for the fetch to end");
        Assert.That(heldAfterFetch, Is.EqualTo(parentWaitsForData ? 1 : 0), "the block waits for its parent's retry");
        Assert.That(fetchesWhileWaiting, Is.EqualTo(1), "the fetch still running keeps its place in the bound");
        Assert.That(harness.Importer.Known, Does.Contain(child.ComputeMessageRoot()));
        Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.Zero, "nothing is held for a parent that already imported");
    }

    // A forgery of the ancestor queued while its fetch ran does not cost the genuine copy the fetch returns: that copy waits beside
    // the forgery, kept only once, and takes its place when it fails; the walk's block imports with it.
    [Test]
    public async Task Genuine_ancestor_fetched_while_a_forgery_of_it_waits_imports_with_the_walk([Values] bool genuineAlreadyWaiting)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        Hash256 parentRoot = parent.ComputeMessageRoot();
        BlsSignature forgedSignature = new(Enumerable.Repeat((byte)0x11, 96).ToArray());
        ForkedSignedBeaconBlock forgery = new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain[0].Message, Signature = forgedSignature });
        (Harness harness, IBeaconSyncPeer _, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches) = CreateWaitingByRootHarness();
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Unavailable.Add(parentRoot);
        harness.Importer.ForgedSignatures.Add(forgedSignature);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        await harness.Orchestrator.ProcessGossipBlockAsync(child, cts.Token);
        await harness.Orchestrator.ImportBlockAsync(forgery, cts.Token);
        if (genuineAlreadyWaiting)
        {
            await harness.Orchestrator.ImportBlockAsync(parent, cts.Token, fetchedByRoot: true);
        }

        fetches[parentRoot].SetResult([parent]);
        await harness.Orchestrator.SettleWithinAsync(maxPasses: 10, cts.Token);
        int waiting = harness.Orchestrator.PendingRetryBlockCount;
        harness.Importer.Unavailable.Remove(parentRoot);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, cts.Token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(waiting, Is.EqualTo(2), "the fetched copy waits beside the forgery");
        Assert.That(harness.Importer.Known, Does.Contain(parentRoot).And.Contain(child.ComputeMessageRoot()));
        Assert.That(harness.Importer.ByRootImports.Count(r => r == parentRoot), Is.EqualTo(2), "the genuine copy is tried once on arrival and once in the forgery's place");
    }

    // An ancestor queued from gossip while its fetch ran, and fetched as the same signed block, retries as fetched, also when it
    // waits as a copy behind a queued forgery: a spent gossip regeneration budget defers it instead of dropping it and the walk's block.
    [Test]
    public async Task Ancestor_queued_from_gossip_and_fetched_while_waiting_retries_as_fetched([Values] bool behindForgery)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        Hash256 parentRoot = parent.ComputeMessageRoot();
        (Harness harness, IBeaconSyncPeer _, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches) = CreateWaitingByRootHarness();
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Unavailable.Add(parentRoot);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        BlsSignature forgedSignature = new(Enumerable.Repeat((byte)0x11, 96).ToArray());
        harness.Importer.ForgedSignatures.Add(forgedSignature);

        await harness.Orchestrator.ProcessGossipBlockAsync(child, cts.Token);
        if (behindForgery)
        {
            await harness.Orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain[0].Message, Signature = forgedSignature }), cts.Token);
        }

        await harness.Orchestrator.ImportBlockAsync(parent, cts.Token);
        fetches[parentRoot].SetResult([parent]);
        await harness.Orchestrator.SettleWithinAsync(maxPasses: 10, cts.Token);
        harness.Importer.Unavailable.Remove(parentRoot);
        harness.Importer.RegenerationRefused.Add(parentRoot);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, cts.Token);
        harness.Importer.RegenerationRefused.Remove(parentRoot);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, cts.Token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Known, Does.Contain(parentRoot).And.Contain(child.ComputeMessageRoot()));
        Assert.That(harness.Importer.ByRootImports, Does.Contain(parentRoot), "the waiting copy retries as fetched");
    }

    // A fetched ancestor that waits for a retry holds the walk's blocks until it imports, whatever it waits for.
    [Test]
    public async Task Walk_holds_its_blocks_behind_a_fetched_ancestor_waiting_for_a_retry([Values] RetryCause cause)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        Hash256 parentRoot = parent.ComputeMessageRoot();
        (Harness harness, IBeaconSyncPeer _) = CreateBackfillHarness(parent);
        harness.Importer.Known.Add(anchorRoot);
        HashSet<Hash256> waitsFor = cause switch
        {
            RetryCause.Data => harness.Importer.Unavailable,
            RetryCause.Engine => harness.Importer.EngineDown,
            _ => harness.Importer.Early,
        };
        waitsFor.Add(parentRoot);
        if (cause == RetryCause.Slot)
        {
            harness.Importer.Ticks.Clear();
            harness.Importer.Ticks.Add(parent.Slot - 1);
        }

        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(child, CancellationToken.None);
        int held = harness.Orchestrator.PendingGossipBlockCount;
        waitsFor.Remove(parentRoot);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(held, Is.EqualTo(1), "the gossip block waits for its fetched parent's retry");
        Assert.That(harness.Importer.Known, Does.Contain(parentRoot).And.Contain(child.ComputeMessageRoot()));
        Assert.That(harness.Importer.ByRootImports, Does.Contain(parentRoot), "the fetched parent retries as fetched");
    }

    public enum RetryCause
    {
        Data,
        Engine,
        Slot,
    }

    // A fault outside the per-peer catch must still free the fetch's place, or each one would shrink the bound until restart.
    [Test]
    public async Task A_faulted_ancestor_fetch_frees_its_place_and_holds_its_block()
    {
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ForkedSignedBeaconBlock>>(new InvalidOperationException("Channel closed")));
        peer.When(static p => p.ReportFailure(Arg.Any<PeerFailureReason>(), Arg.Any<string?>())).Do(static _ => throw new InvalidOperationException("peer bookkeeping failed"));
        Harness harness = CreateHarness(anchorSlot: NearHeadAnchorSlot, peers: [peer]);
        const int Faults = BeaconSyncOrchestrator.MaxConcurrentAncestorFetches + 1;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        for (int i = 0; i < Faults; i++)
        {
            ulong slot = WallSlot + (ulong)(i / BeaconSyncOrchestrator.MaxBackfillsPerSlot);
            SetWallSlot(harness, slot);
            await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(UnknownParentBlock(slot, i), cts.Token);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Orchestrator.AncestorFetchesInFlight, Is.Zero);
        Assert.That(ByRootRequests(peer), Is.EqualTo(Faults), "faulted fetches do not use up the bound");
        Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.EqualTo(Faults), "each block is held for range sync");
    }

    /// <summary>A node near the head with one peer whose by-root request for each root waits until the test completes it.</summary>
    private static (Harness Harness, IBeaconSyncPeer Peer, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> Fetches) CreateWaitingByRootHarness()
    {
        Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches = [];
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>> fetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (fetches)
            {
                fetches[call.Arg<Hash256[]>()[0]] = fetch;
            }

            return fetch.Task;
        });
        return (CreateHarness(anchorSlot: NearHeadAnchorSlot, peers: [peer]), peer, fetches);
    }

    /// <summary>Processes queued work until the walk has asked for <paramref name="root"/>.</summary>
    private static async Task WaitForFetchAsync(Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches, Hash256 root, Harness harness, CancellationToken token)
    {
        for (int passes = 0; ; passes++)
        {
            lock (fetches)
            {
                if (fetches.ContainsKey(root))
                {
                    return;
                }
            }

            if (passes == 10)
            {
                throw new InvalidOperationException($"No fetch of {root} after {passes} passes");
            }

            await harness.Orchestrator.WaitForWorkAsync(token);
            await harness.Orchestrator.ProcessQueuedAsync(token);
        }
    }

    /// <summary>Ends every by-root request still waiting with no blocks, so a settle cannot wait on one a regression started.</summary>
    private static void CompleteOutstandingFetches(Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches)
    {
        lock (fetches)
        {
            foreach (TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>> fetch in fetches.Values)
            {
                fetch.TrySetResult([]);
            }
        }
    }

    private static void SetWallSlot(Harness harness, ulong slot) =>
        harness.Timestamper.Set(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + slot * Spec.SecondsPerSlot));
}
