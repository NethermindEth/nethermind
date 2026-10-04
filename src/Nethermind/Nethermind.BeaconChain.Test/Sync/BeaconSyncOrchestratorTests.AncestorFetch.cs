// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core.Crypto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>A by-root ancestor fetch can wait out several peers' timeouts, so it runs off the worker, which keeps importing and ticking meanwhile.</summary>
public partial class BeaconSyncOrchestratorTests
{

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
