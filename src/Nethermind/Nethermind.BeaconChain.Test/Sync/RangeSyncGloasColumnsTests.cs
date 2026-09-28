// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// <see cref="RangeSync"/> fetches a Gloas block's sampled columns and verifies each against the bid of the
/// block it names, since a Gloas sidecar carries neither commitments nor a signed header of its own.
/// </summary>
public class RangeSyncGloasColumnsTests
{
    private const ulong AnchorSlot = 16;
    private const ulong FuluSlot = 31;
    private const ulong GloasSlot = 32;

    /// <summary>Fulu from genesis and Gloas from epoch 1, so slots 31 and 32 straddle the fork inside one batch.</summary>
    private static BeaconChainSpec Spec { get; } = new()
    {
        ChainId = ImportableBlobBlock.FuluFromGenesis.ChainId,
        CheckpointSyncUrl = ImportableBlobBlock.FuluFromGenesis.CheckpointSyncUrl,
        Bootnodes = ImportableBlobBlock.FuluFromGenesis.Bootnodes,
        SecondsPerSlot = ImportableBlobBlock.FuluFromGenesis.SecondsPerSlot,
        SlotsPerEpoch = ImportableBlobBlock.FuluFromGenesis.SlotsPerEpoch,
        GenesisTime = ImportableBlobBlock.FuluFromGenesis.GenesisTime,
        GenesisValidatorsRoot = ImportableBlobBlock.FuluFromGenesis.GenesisValidatorsRoot,
        Forks = ImportableBlobBlock.FuluFromGenesis.Forks,
        BlobSchedule = ImportableBlobBlock.FuluFromGenesis.BlobSchedule,
        ElectraForkEpoch = 0,
        FuluForkEpoch = 0,
        MaxBlobsPerBlockElectra = ImportableBlobBlock.FuluFromGenesis.MaxBlobsPerBlockElectra,
        GloasForkEpoch = 1,
        GloasForkVersion = ImportableBlobBlock.FuluFromGenesis.GloasForkVersion,
    };

    private static readonly Hash256 OtherRoot = new([.. Enumerable.Repeat((byte)0xB7, 32)]);

    public enum BadSidecar
    {
        WrongSlot,
        RootOutsideBatch,
        UnrequestedColumn,
        TamperedProof,
    }

    public enum WindowClock
    {
        None,
        InsideWindow,
        PastWindow,
    }

    public enum ByRootShortcut
    {
        NoCustodyIdentity,
        BeforeWindow,
        NoBlobs,
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Batch_straddling_the_fork_issues_one_request_per_fork_each_inside_its_fork(CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        List<(ulong StartSlot, ulong Count)> fuluWindows = [];
        List<(ulong StartSlot, ulong Count, ulong[] Columns)> gloasWindows = [];
        RangeSyncTests.StubPeer peer = chain.CreatePeer(
            (startSlot, count, columns) =>
            {
                gloasWindows.Add((startSlot, count, columns));
                return [.. columns.Select(c => chain.GloasSidecar(c))];
            },
            fuluWindows);
        DataColumnSidecarPool pool = new();

        List<ForkedSignedBeaconBlock> yielded = await RunAsync(peer, pool, discovery, clock: null, chain, token);

        ulong[] sampled = [.. SampledColumns(discovery)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(yielded, Has.Count.EqualTo(2), "both blocks are yielded");
            Assert.That(fuluWindows, Is.EqualTo(new[] { (FuluSlot, 1UL) }), "one Fulu request, over the Fulu block only");
            Assert.That(gloasWindows.Select(static w => (w.StartSlot, w.Count)), Is.EqualTo(new[] { (GloasSlot, 1UL) }), "one Gloas request, over the Gloas block only");
            Assert.That(Spec.ForkAtEpoch(Spec.GetEpoch(fuluWindows[0].StartSlot + fuluWindows[0].Count - 1)), Is.EqualTo(BeaconFork.Fulu), "the Fulu window ends before the fork");
            Assert.That(Spec.ForkAtEpoch(Spec.GetEpoch(gloasWindows[0].StartSlot)), Is.EqualTo(BeaconFork.Gloas), "the Gloas window starts at or after the fork");
            Assert.That(gloasWindows[0].Columns, Is.EquivalentTo(sampled), "the node's sampled columns are requested");
            Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True, "every verified Gloas sidecar is pooled");
            Assert.That(peer.Failures, Is.Zero);
        }
    }

    /// <summary>
    /// Each rule is the only one the sidecar breaks, so removing it lets the sidecar into the pool. The slot rule matters
    /// because KZG does not bind the slot and the served-map availability path does not re-check it.
    /// </summary>
    [TestCase(BadSidecar.WrongSlot)]
    [TestCase(BadSidecar.RootOutsideBatch)]
    [TestCase(BadSidecar.UnrequestedColumn)]
    [TestCase(BadSidecar.TamperedProof)]
    [CancelAfter(30_000)]
    public async Task A_range_sidecar_breaking_one_rule_is_not_pooled_and_penalizes_the_peer(BadSidecar bad, CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        ulong victim = bad == BadSidecar.UnrequestedColumn ? UnsampledColumn(sampled) : sampled[0];
        DataColumnSidecarGloas forged = Forge(bad, chain.GloasSidecar(victim));
        RangeSyncTests.StubPeer peer = chain.CreatePeer((_, _, columns) => [forged, .. columns.Where(c => c != victim).Select(c => chain.GloasSidecar(c))]);
        DataColumnSidecarPool pool = new();

        List<ForkedSignedBeaconBlock> yielded = await RunAsync(peer, pool, discovery, clock: null, chain, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(yielded, Has.Count.EqualTo(2), "a bad sidecar never fails the batch");
            Assert.That(pool.TryGetGloas(forged.BeaconBlockRoot!, victim, out _), Is.False, "the bad sidecar is not pooled");
            Assert.That(pool.TryGetGloas(chain.GloasRoot, victim, out _), Is.False);
            Assert.That(sampled.Where(c => c != victim).All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True, "the peer's valid sidecars still count");
            Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
        }
    }

    /// <summary>The batch block is not state-validated, so its bid slot may differ from its slot; the sidecar must name the block slot.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_range_sidecar_must_name_the_block_slot_even_when_the_bid_names_another(CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create(bidSlot: GloasSlot + 1);
        ulong[] sampled = [.. SampledColumns(discovery)];
        DataColumnSidecarGloas atBidSlot = DataColumnSidecarGloasTestFixture.BuildSidecar(sampled[0], GloasSlot + 1, chain.GloasRoot);
        RangeSyncTests.StubPeer peer = chain.CreatePeer((_, _, columns) => [atBidSlot, .. columns.Select(c => chain.GloasSidecar(c))]);
        DataColumnSidecarPool pool = new();

        await RunAsync(peer, pool, discovery, clock: null, chain, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out DataColumnSidecarGloas? held) && held.Slot == GloasSlot), Is.True, "sidecars naming the block slot are pooled");
            Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }), "the sidecar naming the bid slot is rejected");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_failed_range_request_penalizes_the_peer_and_still_yields_the_batch(CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        RangeSyncTests.StubPeer peer = chain.CreatePeer(static (_, _, _) => throw new IOException("connection reset"));

        List<ForkedSignedBeaconBlock> yielded = await RunAsync(peer, new DataColumnSidecarPool(), discovery, clock: null, chain, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(yielded, Has.Count.EqualTo(2));
            Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.RequestFailed }));
        }
    }

    [TestCase(WindowClock.None, 1)]
    [TestCase(WindowClock.InsideWindow, 1)]
    [TestCase(WindowClock.PastWindow, 0)]
    [CancelAfter(30_000)]
    public async Task Gloas_blocks_before_the_data_availability_window_get_no_column_request(WindowClock setting, int expectedRequests, CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        int gloasRequests = 0;
        RangeSyncTests.StubPeer peer = chain.CreatePeer((_, _, columns) =>
        {
            gloasRequests++;
            return [.. columns.Select(c => chain.GloasSidecar(c))];
        });

        await RunAsync(peer, new DataColumnSidecarPool(), discovery, CreateClock(setting), chain, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gloasRequests, Is.EqualTo(expectedRequests));
            Assert.That(peer.Failures, Is.Zero);
        }
    }

    [Test]
    public async Task By_root_fetch_requests_only_the_missing_columns()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        DataColumnSidecarPool pool = new();
        foreach (ulong column in sampled.Take(sampled.Length / 2))
        {
            pool.AddGloas(chain.GloasSidecar(column));
        }

        List<DataColumnsByRootIdentifier[]> requests = [];
        RangeSyncTests.StubPeer peer = chain.CreateRootPeer("peer", ids =>
        {
            requests.Add(ids);
            return [.. ids[0].Columns!.Select(c => chain.GloasSidecar(c))];
        });

        bool available = await CreateSync(pool, discovery, clock: null, peer).FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.True);
            Assert.That(requests, Has.Count.EqualTo(1));
            Assert.That(requests[0], Has.Length.EqualTo(1), "one identifier for the one block");
            Assert.That(requests[0][0].BlockRoot, Is.EqualTo(chain.GloasRoot));
            Assert.That(requests[0][0].Columns, Is.EquivalentTo(sampled.Skip(sampled.Length / 2)), "held columns are not requested again");
            Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True);
        }
    }

    [Test]
    public async Task A_bad_by_root_sidecar_penalizes_its_peer_and_another_peers_copy_is_pooled([Values(BadSidecar.WrongSlot, BadSidecar.RootOutsideBatch, BadSidecar.TamperedProof)] BadSidecar bad)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        ulong victim = sampled[0];
        List<ulong[]> secondPeerRequests = [];
        RangeSyncTests.StubPeer lying = chain.CreateRootPeer("lying", ids =>
            [.. ids[0].Columns!.Select(c => c == victim ? Forge(bad, chain.GloasSidecar(c)) : chain.GloasSidecar(c))]);
        RangeSyncTests.StubPeer honest = chain.CreateRootPeer("honest", ids =>
        {
            secondPeerRequests.Add(ids[0].Columns!);
            return [.. ids[0].Columns!.Select(c => chain.GloasSidecar(c))];
        });
        DataColumnSidecarPool pool = new();

        bool available = await CreateSync(pool, discovery, clock: null, lying, honest).FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.True);
            Assert.That(lying.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
            Assert.That(secondPeerRequests, Is.EqualTo(new[] { sampled }), "the peers are asked at once, each for every missing column");
            Assert.That(pool.TryGetGloas(chain.GloasRoot, victim, out DataColumnSidecarGloas? held) ? held : null, Is.Not.Null.And.Property(nameof(DataColumnSidecarGloas.Slot)).EqualTo(GloasSlot), "the honest sidecar is pooled");
            Assert.That(pool.TryGetGloas(OtherRoot, victim, out _), Is.False, "a sidecar for another block is not pooled");
            Assert.That(honest.Failures, Is.Zero);
        }
    }

    /// <summary>A column an earlier reply of the same fetch supplied is neither verified again nor held against a later peer's copy.</summary>
    [Test]
    public async Task A_by_root_copy_of_a_column_already_supplied_is_skipped_without_verification()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        Dictionary<ulong, DataColumnSidecarGloas> served = [];
        RangeSyncTests.StubPeer honest = chain.CreateRootPeer("honest", ids =>
        {
            foreach (ulong column in ids[0].Columns!)
            {
                served[column] = chain.GloasSidecar(column);
            }

            return [.. ids[0].Columns!.Select(c => served[c])];
        });
        RangeSyncTests.StubPeer late = chain.CreateRootPeer("late", ids => [.. ids[0].Columns!.Select(c => Forge(BadSidecar.TamperedProof, chain.GloasSidecar(c)))]);
        DataColumnSidecarPool pool = new();

        bool available = await CreateSync(pool, discovery, clock: null, honest, late).FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.True);
            Assert.That(late.Reports, Is.Empty, "the tampered copies were never verified");
            Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out DataColumnSidecarGloas? held) && ReferenceEquals(held, served[c])), Is.True, "the first reply's sidecars stay pooled");
        }
    }

    [Test]
    public async Task A_failed_by_root_request_penalizes_its_peer_while_another_peer_serves_the_columns()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        RangeSyncTests.StubPeer failing = chain.CreateRootPeer("failing", static _ => throw new IOException("connection reset"));
        RangeSyncTests.StubPeer honest = chain.CreateRootPeer("honest", ids => [.. ids[0].Columns!.Select(c => chain.GloasSidecar(c))]);
        DataColumnSidecarPool pool = new();

        bool available = await CreateSync(pool, discovery, clock: null, failing, honest).FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.True);
            Assert.That(failing.Reports, Is.EqualTo(new[] { PeerFailureReason.RequestFailed }));
            Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True);
            Assert.That(honest.Failures, Is.Zero);
        }
    }

    /// <summary>gloas/p2p-interface.md DataColumnSidecarsByRoot names the block by root, so a peer whose last status head is behind the block is still asked.</summary>
    [Test]
    public async Task By_root_fetch_asks_a_peer_whose_recorded_head_is_behind_the_block()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        RangeSyncTests.StubPeer behind = chain.CreateRootPeer("behind", ids => [.. ids[0].Columns!.Select(c => chain.GloasSidecar(c))], headSlot: FuluSlot);
        DataColumnSidecarPool pool = new();

        bool available = await CreateSync(pool, discovery, clock: null, behind).FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.True);
            Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True);
        }
    }

    /// <summary>No peer is asked while every sampled column is held; otherwise every peer of the fetch is asked at once.</summary>
    [Test]
    public async Task By_root_fetch_asks_every_peer_at_once_unless_every_column_is_held([Values] bool heldOnEntry)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        DataColumnSidecarPool pool = new();
        if (heldOnEntry)
        {
            foreach (ulong column in sampled)
            {
                pool.AddGloas(chain.GloasSidecar(column));
            }
        }

        int[] requests = new int[2];
        RangeSyncTests.StubPeer[] peers = [.. Enumerable.Range(0, requests.Length).Select(i => chain.CreateRootPeer($"peer{i}", ids =>
        {
            requests[i]++;
            return [.. ids[0].Columns!.Select(c => chain.GloasSidecar(c))];
        }))];

        bool available = await CreateSync(pool, discovery, clock: null, peers).FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.True);
            Assert.That(requests, Is.EqualTo(heldOnEntry ? new[] { 0, 0 } : new[] { 1, 1 }));
        }
    }

    [Test]
    public async Task By_root_fetch_returns_false_while_a_column_is_missing_after_three_peers()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        ulong withheld = sampled[^1];
        int[] requests = new int[4];
        RangeSyncTests.StubPeer[] peers = [.. Enumerable.Range(0, requests.Length).Select(i => chain.CreateRootPeer($"peer{i}", ids =>
        {
            requests[i]++;
            return [.. ids[0].Columns!.Where(c => c != withheld).Select(c => chain.GloasSidecar(c))];
        }))];
        DataColumnSidecarPool pool = new();

        bool available = await CreateSync(pool, discovery, clock: null, peers).FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.False);
            Assert.That(requests, Is.EqualTo(new[] { 1, 1, 1, 0 }), "at most three peers are asked");
            Assert.That(pool.TryGetGloas(chain.GloasRoot, withheld, out _), Is.False);
            Assert.That(sampled.Where(c => c != withheld).All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True);
        }
    }

    /// <summary>The by-root requests of one fetch run together, so peers that never answer cost one request timeout between them, not one each.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task By_root_peers_that_never_answer_cost_one_request_timeout_per_fetch(CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        TimeSpan requestTimeout = TimeSpan.FromSeconds(5);
        const int PeersPerFetch = 3;
        DeferredBlockColumnFetchTests.Gate allAsked = new(PeersPerFetch);
        DeferredBlockColumnFetchTests.UnansweringPeer[] peers = [.. Enumerable.Range(0, PeersPerFetch).Select(i => new DeferredBlockColumnFetchTests.UnansweringPeer($"unanswering-{i}", sampled, allAsked, requestTimeout))];

        Stopwatch elapsed = Stopwatch.StartNew();
        bool available = await CreateSync(new DataColumnSidecarPool(), discovery, clock: null, peers).FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, token);
        elapsed.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.False);
            Assert.That(allAsked.MaxInFlight, Is.EqualTo(PeersPerFetch), "every peer of the fetch is asked before any answers");
            Assert.That(elapsed.Elapsed, Is.LessThan(requestTimeout), "no request waited out its timeout behind another");
            Assert.That(peers.Select(static p => p.Failures), Is.All.EqualTo(1));
        }
    }

    /// <summary>Repeated fetches for one block rotate through the peers, so peers answering with nothing do not hide one that serves the columns.</summary>
    [Test]
    public async Task By_root_fetches_sharing_a_rotation_reach_the_peer_behind_three_that_answer_nothing()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        int[] requests = new int[4];
        RangeSyncTests.StubPeer[] peers = [.. Enumerable.Range(0, requests.Length).Select(i => chain.CreateRootPeer($"peer{i}", ids =>
        {
            requests[i]++;
            return i == requests.Length - 1 ? [.. ids[0].Columns!.Select(c => chain.GloasSidecar(c))] : [];
        }))];
        DataColumnSidecarPool pool = new();
        RangeSync sync = CreateSync(pool, discovery, clock: null, peers);
        RangeSync.ColumnFetchRotation rotation = new(RangeSyncTests.ClockAtGenesis(Spec));

        bool first = await sync.FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, rotation, CancellationToken.None);
        bool second = await sync.FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, rotation, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((first, second), Is.EqualTo((false, true)));
            Assert.That(requests, Is.EqualTo(new[] { 1, 1, 1, 1 }), "the second fetch asks only the peer the first did not");
            Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True);
        }
    }

    /// <summary>Mirrors <see cref="GloasCustodySamplingAvailability.IsDataAvailable"/>: nothing is demanded without blobs or before the window, and nothing can be proven without an identity.</summary>
    [TestCase(ByRootShortcut.NoCustodyIdentity, false)]
    [TestCase(ByRootShortcut.BeforeWindow, true)]
    [TestCase(ByRootShortcut.NoBlobs, true)]
    public async Task By_root_fetch_shortcuts_without_asking_a_peer(ByRootShortcut shortcut, bool expected)
    {
        StraddlingChain chain = StraddlingChain.Create();
        int requests = 0;
        RangeSyncTests.StubPeer peer = chain.CreateRootPeer("peer", ids =>
        {
            requests++;
            return [.. ids[0].Columns!.Select(c => chain.GloasSidecar(c))];
        });
        SlotClock? clock = shortcut == ByRootShortcut.BeforeWindow ? CreateClock(WindowClock.PastWindow) : null;
        ExecutionPayloadBid bid = shortcut == ByRootShortcut.NoBlobs ? new ExecutionPayloadBid { Slot = GloasSlot, BlobKzgCommitments = [] } : chain.Bid;

        bool available = await CreateSync(new DataColumnSidecarPool(), discovery: null, clock, peer).FetchGloasColumnsByRootAsync(chain.GloasRoot, bid, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.EqualTo(expected));
            Assert.That(requests, Is.Zero);
        }
    }

    private static DataColumnSidecarGloas Forge(BadSidecar bad, DataColumnSidecarGloas sidecar)
    {
        switch (bad)
        {
            case BadSidecar.WrongSlot:
                sidecar.Slot = GloasSlot + 1;
                break;
            case BadSidecar.RootOutsideBatch:
                sidecar.BeaconBlockRoot = OtherRoot;
                break;
            case BadSidecar.TamperedProof:
                sidecar.KzgProofs = [sidecar.KzgProofs![1], sidecar.KzgProofs[0]];
                break;
        }

        return sidecar;
    }

    private static ulong UnsampledColumn(ulong[] sampled) =>
        Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).First(c => !sampled.Contains(c));

    private static async Task<List<ForkedSignedBeaconBlock>> RunAsync(RangeSyncTests.StubPeer peer, DataColumnSidecarPool pool, BeaconDiscovery discovery, SlotClock? clock, StraddlingChain chain, CancellationToken token)
    {
        List<ForkedSignedBeaconBlock> yielded = [];
        await foreach (ForkedSignedBeaconBlock block in CreateSync(pool, discovery, clock, peer).Run(chain.AnchorRoot, AnchorSlot, static () => GloasSlot, token))
        {
            yielded.Add(block);
        }

        return yielded;
    }

    private static RangeSync CreateSync(DataColumnSidecarPool pool, BeaconDiscovery? discovery, SlotClock? clock, params IBeaconSyncPeer[] peers) =>
        new(new RangeSyncTests.StubPool(peers), LimboLogs.Instance, pool, Spec, clock ?? RangeSyncTests.ClockAtGenesis(Spec), discovery);

    /// <summary>Resolves the identity and local custody exactly as Start does, without binding a socket.</summary>
    private static BeaconDiscovery CreateDiscovery()
    {
        BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new RangeSyncTests.FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        return discovery;
    }

    private static IReadOnlyList<ulong> SampledColumns(BeaconDiscovery discovery) => new DiscoveryNodeCustodySource(discovery).Current!.SampledColumns;

    private static SlotClock? CreateClock(WindowClock setting)
    {
        ulong gloasEpoch = Spec.GetEpoch(GloasSlot);
        ulong? epoch = setting switch
        {
            WindowClock.InsideWindow => gloasEpoch + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests,
            WindowClock.PastWindow => gloasEpoch + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 1,
            _ => null,
        };

        return epoch is { } e
            ? new SlotClock(Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + e * Spec.SlotsPerEpoch * Spec.SecondsPerSlot)).UtcDateTime))
            : null;
    }

    /// <summary>A Fulu blob block at the last Fulu slot and its Gloas blob child at the fork slot, above a Fulu anchor.</summary>
    private sealed class StraddlingChain
    {
        public required Hash256 AnchorRoot { get; init; }
        public required ForkedSignedBeaconBlock[] Blocks { get; init; }
        public required Hash256 GloasRoot { get; init; }
        public required ExecutionPayloadBid Bid { get; init; }

        public static StraddlingChain Create(ulong bidSlot = GloasSlot)
        {
            SignedBeaconBlock anchor = TestChain.CreateBlock(AnchorSlot, Hash256.Zero);
            Hash256 anchorRoot = SszRoots.HashTreeRoot(anchor.Message!);
            SignedBeaconBlock fulu = TestChain.CreateBlock(FuluSlot, anchorRoot);
            fulu.Message!.Body!.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
            SignedBeaconBlockGloas gloas = CreateMinimalGloasBlock(GloasSlot, SszRoots.HashTreeRoot(fulu.Message));
            ExecutionPayloadBid bid = gloas.Message!.Body!.SignedExecutionPayloadBid!.Message!;
            bid.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
            bid.Slot = bidSlot;
            return new StraddlingChain
            {
                AnchorRoot = anchorRoot,
                Blocks = [new ForkedSignedBeaconBlock.OfFulu(fulu), new ForkedSignedBeaconBlock.OfGloas(gloas)],
                GloasRoot = SszRoots.HashTreeRoot(gloas.Message),
                Bid = bid,
            };
        }

        public DataColumnSidecarGloas GloasSidecar(ulong column) => DataColumnSidecarGloasTestFixture.BuildSidecar(column, GloasSlot, GloasRoot);

        /// <summary>Serves <see cref="Blocks"/> by range and no Fulu sidecars, recording each Fulu column window.</summary>
        public RangeSyncTests.StubPeer CreatePeer(Func<ulong, ulong, ulong[], DataColumnSidecarGloas[]> gloasColumnHandler, List<(ulong StartSlot, ulong Count)>? fuluWindows = null) => new(
            "peer",
            headSlot: GloasSlot,
            (startSlot, count) => [.. Blocks.Where(b => b.Slot >= startSlot && b.Slot - startSlot < count)],
            (startSlot, count, _) =>
            {
                fuluWindows?.Add((startSlot, count));
                return [];
            },
            gloasColumnHandler);

        public RangeSyncTests.StubPeer CreateRootPeer(string id, Func<DataColumnsByRootIdentifier[], DataColumnSidecarGloas[]> gloasRootHandler, ulong headSlot = GloasSlot) => new(
            id,
            headSlot,
            static (_, _) => [],
            gloasRootHandler: gloasRootHandler);
    }
}
