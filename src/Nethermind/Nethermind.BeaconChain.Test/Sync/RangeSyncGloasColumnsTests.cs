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
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
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
    private const ulong SecondGloasSlot = 33;

    /// <summary>Fulu from genesis and Gloas from epoch 1, so slots 31 and 32 straddle the fork inside one batch.</summary>
    internal static BeaconChainSpec Spec { get; } = new()
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
        MalformedColumn,
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
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(yielded, Has.Count.EqualTo(2), "both blocks are yielded");
        Assert.That(fuluWindows, Is.EqualTo(new[] { (FuluSlot, 1UL) }), "one Fulu request, over the Fulu block only");
        Assert.That(gloasWindows.Select(static w => (w.StartSlot, w.Count)), Is.EqualTo(new[] { (GloasSlot, 1UL) }), "one Gloas request, over the Gloas block only");
        Assert.That(Spec.ForkAtEpoch(Spec.GetEpoch(fuluWindows[0].StartSlot + fuluWindows[0].Count - 1)), Is.EqualTo(BeaconFork.Fulu), "the Fulu window ends before the fork");
        Assert.That(Spec.ForkAtEpoch(Spec.GetEpoch(gloasWindows[0].StartSlot)), Is.EqualTo(BeaconFork.Gloas), "the Gloas window starts at or after the fork");
        Assert.That(gloasWindows[0].Columns, Is.EquivalentTo(sampled), "the node's sampled columns are requested");
        Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True, "every verified Gloas sidecar is pooled");
        Assert.That(peer.Failures, Is.Zero);
    }

    /// <summary>
    /// Each rule is the only one the sidecar breaks, so removing it lets the sidecar into the pool. The slot rule matters
    /// because KZG does not bind the slot and the served-map availability path does not re-check it.
    /// </summary>
    [TestCase(BadSidecar.WrongSlot)]
    [TestCase(BadSidecar.RootOutsideBatch)]
    [TestCase(BadSidecar.UnrequestedColumn)]
    [TestCase(BadSidecar.TamperedProof)]
    [TestCase(BadSidecar.MalformedColumn)]
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(yielded, Has.Count.EqualTo(2), "a bad sidecar never fails the batch");
        Assert.That(pool.TryGetGloas(forged.BeaconBlockRoot!, victim, out _), Is.False, "the bad sidecar is not pooled");
        Assert.That(pool.TryGetGloas(chain.GloasRoot, victim, out _), Is.False);
        Assert.That(sampled.Where(c => c != victim).All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True, "the peer's valid sidecars still count");
        Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out DataColumnSidecarGloas? held) && held.Slot == GloasSlot), Is.True, "sidecars naming the block slot are pooled");
        Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }), "the sidecar naming the bid slot is rejected");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_failed_range_request_penalizes_the_peer_and_still_yields_the_batch(CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        RangeSyncTests.StubPeer peer = chain.CreatePeer(static (_, _, _) => throw new IOException("connection reset"));

        List<ForkedSignedBeaconBlock> yielded = await RunAsync(peer, new DataColumnSidecarPool(), discovery, clock: null, chain, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(yielded, Has.Count.EqualTo(2));
        Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.RequestFailed }));
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(gloasRequests, Is.EqualTo(expectedRequests));
        Assert.That(peer.Failures, Is.Zero);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Gloas_range_asks_only_custodians_and_only_for_the_columns_still_missing(CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        ulong[] held = sampled[..(sampled.Length / 2)];
        DataColumnSidecarPool pool = new();
        foreach (ulong column in held)
        {
            pool.AddGloas(chain.GloasSidecar(column));
        }

        ulong[] unsampled = [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).Except(sampled)];
        RangeSyncTests.StubPeer[] peers =
        [
            chain.HonestPeer("a", new PeerColumnCustody(sampled.Where(static (_, i) => i % 2 == 0), isAdvertised: true)),
            chain.HonestPeer("b", new PeerColumnCustody(sampled.Where(static (_, i) => i % 2 == 1), isAdvertised: true)),
            chain.HonestPeer("unsampled", new PeerColumnCustody(unsampled[..4], isAdvertised: true)),
            chain.HonestPeer("unknown", PeerColumnCustody.None),
        ];

        List<ForkedSignedBeaconBlock> yielded = await RunAsync(pool, discovery, clock: null, chain, GloasSlot, token, peers);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(yielded, Has.Count.EqualTo(2));
        Assert.That(peers.Where(static p => p.RequestedGloasColumns.Count > 0).Select(static p => p.Id), Is.SubsetOf(new[] { "a", "b" }), "a peer custodying no missing column gets no request");
        foreach (RangeSyncTests.StubPeer peer in peers)
        {
            Assert.That(peer.RequestedGloasColumns.SelectMany(static c => c), Is.All.Matches<ulong>(peer.Custody.Custodies), $"{peer.Id} is asked only for columns it custodies");
        }

        Assert.That(peers.SelectMany(static p => p.RequestedGloasColumns).SelectMany(static c => c), Is.EquivalentTo(sampled.Except(held)), "every missing column is asked of exactly one custodian and held ones are not asked");
        Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True);
        Assert.That(peers.Sum(static p => p.Failures), Is.Zero);
    }

    /// <summary>Verifying the second copy of a column would fail (tampered proof) and penalize the peer, so no report shows it was skipped.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_duplicate_sidecar_within_one_range_response_is_verified_once(CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        RangeSyncTests.StubPeer peer = chain.CreatePeer((_, _, columns) =>
            [.. columns.Select(c => chain.GloasSidecar(c)), .. columns.Select(c => Forge(BadSidecar.TamperedProof, chain.GloasSidecar(c)))]);
        DataColumnSidecarPool pool = new();

        await RunAsync(peer, pool, discovery, clock: null, chain, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(peer.Reports, Is.Empty, "the second copy of each column was not verified");
        Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True);
    }

    /// <summary>A reply repeating one invalid (root, index) is verified and penalized once, not once per copy: each copy would cost a KZG verification.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Repeated_invalid_copies_of_a_column_in_one_range_response_penalize_the_peer_once(CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong victim = SampledColumns(discovery)[0];
        RangeSyncTests.StubPeer peer = chain.CreatePeer((_, _, columns) =>
            [.. columns.Where(c => c != victim).Select(c => chain.GloasSidecar(c)), .. Enumerable.Range(0, 50).Select(_ => Forge(BadSidecar.TamperedProof, chain.GloasSidecar(victim)))]);

        await RunAsync(peer, new DataColumnSidecarPool(), discovery, clock: null, chain, token);

        Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
    }

    [Test]
    public async Task Repeated_invalid_copies_of_a_column_in_one_by_root_response_penalize_the_peer_once()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong victim = SampledColumns(discovery)[0];
        RangeSyncTests.StubPeer lying = chain.CreateRootPeer("lying", ids =>
            [.. ids[0].Columns!.Where(c => c != victim).Select(c => chain.GloasSidecar(c)), .. Enumerable.Range(0, 50).Select(_ => Forge(BadSidecar.TamperedProof, chain.GloasSidecar(victim)))]);

        await CreateSync(new DataColumnSidecarPool(), discovery, clock: null, lying).FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, CancellationToken.None);

        Assert.That(lying.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
    }

    /// <summary>Only a copy that reached KZG shadows later copies: a cheap rejection must not hide the valid copy that follows it in the same reply.</summary>
    [Test]
    public async Task A_by_root_copy_rejected_before_KZG_does_not_hide_the_valid_copy_after_it([Values(BadSidecar.WrongSlot, BadSidecar.MalformedColumn)] BadSidecar bad)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong victim = SampledColumns(discovery)[0];
        RangeSyncTests.StubPeer peer = chain.CreateRootPeer("peer", ids =>
            [.. Enumerable.Range(0, 3).Select(_ => Forge(bad, chain.GloasSidecar(victim))), .. ids[0].Columns!.Select(c => chain.GloasSidecar(c))]);
        DataColumnSidecarPool pool = new();

        bool available = await CreateSync(pool, discovery, clock: null, peer).FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(available, Is.True, "the valid copy after the rejected ones is pooled");
        Assert.That(pool.TryGetGloas(chain.GloasRoot, victim, out _), Is.True);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_range_copy_rejected_before_KZG_does_not_hide_the_valid_copy_after_it([Values(BadSidecar.WrongSlot, BadSidecar.MalformedColumn)] BadSidecar bad, CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        RangeSyncTests.StubPeer peer = chain.CreatePeer((_, _, columns) =>
            [.. Enumerable.Range(0, 3).Select(_ => Forge(bad, chain.GloasSidecar(sampled[0]))), .. columns.Select(c => chain.GloasSidecar(c))]);
        DataColumnSidecarPool pool = new();

        await RunAsync(peer, pool, discovery, clock: null, chain, token);

        Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True, "the valid copy after the rejected ones is pooled");
    }

    /// <summary>A later round asks exactly the window from the first slot still missing a column to the last one.</summary>
    [TestCase(false, true, GloasSlot, 2UL, TestName = "A_later_range_round_spans_exactly_the_slots_still_lacking_a_column(True,2UL)")]
    [TestCase(false, false, GloasSlot, 1UL, TestName = "A_later_range_round_spans_exactly_the_slots_still_lacking_a_column(False,1UL)")]
    [TestCase(true, true, SecondGloasSlot, 1UL, TestName = "A_later_range_round_requests_only_from_the_first_slot_still_missing_a_column")]
    [CancelAfter(30_000)]
    public async Task A_later_range_round_requests_the_remaining_column_window(
        bool firstSlotComplete, bool secondSlotLacksToo, ulong expectedStart, ulong expectedCount, CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create(withSecondGloas: true, anchorSlot: FuluSlot - 1);
        ulong[] sampled = [.. SampledColumns(discovery)];
        DataColumnSidecarPool pool;
        if (firstSlotComplete)
        {
            ulong lacking = sampled[0];
            pool = new();
            foreach (ulong column in sampled)
            {
                pool.AddGloas(chain.GloasSidecar(column));
                if (column != lacking)
                {
                    pool.AddGloas(chain.SecondGloasSidecar(column));
                }
            }
        }
        else
        {
            pool = new();
            foreach (ulong column in sampled.Skip(1))
            {
                pool.AddGloas(chain.GloasSidecar(column));
                pool.AddGloas(chain.SecondGloasSidecar(column));
            }

            if (!secondSlotLacksToo)
            {
                pool.AddGloas(chain.SecondGloasSidecar(sampled[0]));
            }
        }

        List<(ulong Start, ulong Count)> requests = [];
        RangeSyncTests.StubPeer[] peers = [.. Enumerable.Range(0, 2).Select(i => chain.CreatePeer((start, count, _) =>
        {
            requests.Add((start, count));
            return [];
        }, id: $"peer-{i}", headSlot: SecondGloasSlot))];

        await RunAsync(pool, discovery, clock: null, chain, SecondGloasSlot, token, peers);

        Assert.That(requests, Is.EqualTo(new[] { (GloasSlot, 2UL), (expectedStart, expectedCount) }),
            firstSlotComplete ? "round 0 covers the Gloas blocks, the next round only the slot still lacking the column" : null);
    }

    public enum CustodianReply
    {
        Failed,
        Partial,
        Supernode,
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Range_columns_retain_partial_replies_and_share_custody_without_reusing_failed_peers(
        [Values] bool gloas, [Values] CustodianReply reply, CancellationToken token)
    {
        await using RangeSyncColumnCustodyTests.Fixture? fulu = gloas ? null : RangeSyncColumnCustodyTests.Fixture.Create();
        await using BeaconDiscovery? gloasDiscovery = gloas ? CreateDiscovery() : null;
        StraddlingChain? chain = gloas ? StraddlingChain.Create() : null;
        ulong[] sampled = gloas ? [.. SampledColumns(gloasDiscovery!)] : fulu!.Sampled;
        DataColumnSidecarPool pool = gloas ? new() : fulu!.SidecarPool;
        RangeSyncTests.StubPeer first = reply switch
        {
            CustodianReply.Supernode => gloas ? chain!.HonestPeer("supernode", RangeSyncTests.StubPeer.AllColumns) : fulu!.Peer("supernode", custody: RangeSyncTests.StubPeer.AllColumns),
            CustodianReply.Failed => gloas
                ? chain!.CreatePeer(static (_, _, _) => throw new IOException("connection reset"), id: "failing")
                : fulu!.FailingColumnPeer("failing", static _ => throw new Eth2ReqRespException("Truncated response chunk: Unable to read beyond the end of the stream.")),
            _ => gloas
                ? chain!.CreatePeer((_, _, columns) => [.. columns[1..].Select(chain!.GloasSidecar)], id: "short")
                : fulu!.FailingColumnPeer("cut-short", columns => throw new PartialSidecarsException(
                    new Eth2ReqRespException("Truncated response chunk: Unable to read beyond the end of the stream."), fulu!.ServeColumns(columns[..1]))),
        };
        RangeSyncTests.StubPeer[] others = reply == CustodianReply.Supernode
            ? [.. Enumerable.Range(0, 3).Select(i =>
            {
                PeerColumnCustody custody = new([.. sampled.Where((_, c) => c % 3 == i)], isAdvertised: false);
                return gloas ? chain!.HonestPeer($"floor-{i}", custody) : fulu!.Peer($"floor-{i}", custody: custody);
            })]
            : [gloas ? chain!.HonestPeer("good") : fulu!.Peer("good", custody: RangeSyncTests.StubPeer.AllColumns)];
        RangeSyncTests.StubPeer[] peers = [first, .. others];
        IReadOnlyList<ForkedSignedBeaconBlock> yielded = gloas
            ? await RunAsync(pool, gloasDiscovery!, clock: null, chain!, GloasSlot, token, peers)
            : await fulu!.RunOneRoundAsync(peers, token);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        if (gloas)
        {
            if (reply != CustodianReply.Partial) Assert.That(yielded, Has.Count.EqualTo(2));
            Assert.That(sampled.All(c => pool.TryGetGloas(chain!.GloasRoot, c, out _)), Is.True);
        }
        else
            Assert.That(yielded.Select(b => fulu!.Importer.Import(b, fulu!.Chain.BlockRoot, verifySignatures: true)), Is.EqualTo(new[] { BlockImportResult.Imported }));
        if (reply == CustodianReply.Supernode)
        {
            int share = Math.Max(2, (sampled.Length + 3) / 4);
            ulong[][] firstColumns = gloas ? [.. first.RequestedGloasColumns] : [.. first.RequestedColumns];
            Assert.That(firstColumns.SelectMany(static c => c).Count(), Is.LessThanOrEqualTo(share));
            Assert.That(others.Sum(p => gloas ? p.RequestedGloasColumns.Count : p.ColumnRequests), Is.GreaterThan(0));
            if (gloas) Assert.That(first.RequestedGloasColumns, Is.Not.Empty);
        }
        else if (reply == CustodianReply.Failed)
        {
            Assert.That(gloas ? first.RequestedGloasColumns.Count : first.ColumnRequests, Is.EqualTo(1));
            Assert.That(first.Reports, Is.EqualTo(new[] { PeerFailureReason.RequestFailed }));
            if (gloas)
            {
                Assert.That(others[0].RequestedGloasColumns, Has.Count.EqualTo(2));
                Assert.That(others[0].RequestedGloasColumns.SelectMany(static c => c), Is.EquivalentTo(sampled).And.Unique);
            }
        }
        else if (gloas)
        {
            Assert.That(first.RequestedGloasColumns, Has.Count.EqualTo(1));
            Assert.That(others[0].RequestedGloasColumns.SelectMany(static c => c), Is.EquivalentTo(sampled.Except(first.RequestedGloasColumns[0][1..])));
            Assert.That(first.Failures + others[0].Failures, Is.Zero);
        }
        else
        {
            ulong[] asked = first.RequestedColumns.Single();
            Assert.That(others[0].RequestedColumns.Skip(1).SelectMany(static c => c), Is.EquivalentTo(asked[1..]));
            Assert.That(others[0].RequestedColumns.SelectMany(static c => c), Has.None.EqualTo(asked[0]));
        }
    }

    /// <summary>
    /// The block-only peer supplies no columns. The custodian serves the second Gloas block, either after the batch peer
    /// lacks custody or from inside the requested range; blocks below its earliest slot are left to the by-root retry.
    /// </summary>
    [TestCase(false, TestName = "Gloas_range_asks_a_custodian_rather_than_the_batch_peer")]
    [TestCase(true, TestName = "Gloas_range_falls_back_to_a_custodian_serving_from_inside_the_range")]
    [CancelAfter(30_000)]
    public async Task Gloas_range_requests_columns_from_the_available_custodian(bool fromInside, CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create(withSecondGloas: true, anchorSlot: fromInside ? FuluSlot - 1 : AnchorSlot);
        ulong[] sampled = [.. SampledColumns(discovery)];
        List<(ulong StartSlot, ulong Count, ulong[] Columns)> windows = [];
        RangeSyncTests.StubPeer blocksOnly = chain.CreatePeer(static (_, _, _) => [], id: "blocks", custody: PeerColumnCustody.None, headSlot: SecondGloasSlot);
        RangeSyncTests.StubPeer late = chain.CreatePeer((startSlot, count, columns) =>
        {
            windows.Add((startSlot, count, columns));
            return [.. columns.Select(c => chain.SecondGloasSidecar(c))];
        }, id: "late", earliestAvailableSlot: SecondGloasSlot, headSlot: SecondGloasSlot);
        DataColumnSidecarPool pool = new();

        // With the Fulu anchor, round-robin gives the second batch to the block-only peer.
        RangeSyncTests.StubPeer[] peers = fromInside ? [blocksOnly, late] : [late, blocksOnly];
        List<ForkedSignedBeaconBlock> yielded = await RunAsync(pool, discovery, clock: null, chain, SecondGloasSlot, token, peers);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(yielded, Has.Count.EqualTo(3), fromInside ? "one batch carries all three blocks" : null);
        Assert.That(windows.Select(static w => (w.StartSlot, w.Count)), Is.EqualTo(new[] { (SecondGloasSlot, 1UL) }), fromInside ? "the request starts at the custodian's earliest slot" : null);
        Assert.That(windows[0].Columns, Is.EquivalentTo(sampled));
        Assert.That(sampled.All(c => pool.TryGetGloas(chain.SecondGloasRoot!, c, out _)), Is.True);
        if (fromInside)
            Assert.That(sampled.Any(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.False, "the block below the custodian's earliest slot was not requested");
        Assert.That(late.Failures + blocksOnly.Failures, Is.Zero, fromInside ? null : "the block peer is never asked for columns it does not custody");
    }

    public enum RootColumnCoverage
    {
        HalfHeld,
        BehindHead,
        Missing,
        AllHeld,
        OneWithheld,
    }

    [TestCase(RootColumnCoverage.HalfHeld, 1, TestName = "By_root_fetch_requests_only_the_missing_columns")]
    [TestCase(RootColumnCoverage.BehindHead, 1, TestName = "By_root_fetch_asks_a_peer_whose_recorded_head_is_behind_the_block")]
    [TestCase(RootColumnCoverage.Missing, 2, TestName = "By_root_fetch_asks_every_peer_at_once_unless_every_column_is_held(False)")]
    [TestCase(RootColumnCoverage.AllHeld, 2, TestName = "By_root_fetch_asks_every_peer_at_once_unless_every_column_is_held(True)")]
    [TestCase(RootColumnCoverage.OneWithheld, 4, TestName = "By_root_fetch_returns_false_while_a_column_is_missing_after_three_peers")]
    public async Task By_root_fetch_observes_the_columns_held_and_the_peer_bound(RootColumnCoverage coverage, int peerCount)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        StraddlingChain chain = StraddlingChain.Create();
        ulong[] sampled = [.. SampledColumns(discovery)];
        DataColumnSidecarPool pool = new();
        IEnumerable<ulong> held = coverage switch
        {
            RootColumnCoverage.HalfHeld => sampled.Take(sampled.Length / 2),
            RootColumnCoverage.AllHeld => sampled,
            _ => [],
        };
        foreach (ulong column in held)
        {
            pool.AddGloas(chain.GloasSidecar(column));
        }

        List<DataColumnsByRootIdentifier[]>[] requests = [.. Enumerable.Range(0, peerCount).Select(static _ => new List<DataColumnsByRootIdentifier[]>())];
        RangeSyncTests.StubPeer[] peers = [.. Enumerable.Range(0, peerCount).Select(i => chain.CreateRootPeer(
            coverage == RootColumnCoverage.BehindHead ? "behind" : coverage == RootColumnCoverage.HalfHeld ? "peer" : $"peer{i}", ids =>
            {
                requests[i].Add(ids);
                return [.. ids[0].Columns!.Where(c => coverage != RootColumnCoverage.OneWithheld || c != sampled[^1]).Select(c => chain.GloasSidecar(c))];
            }, headSlot: coverage == RootColumnCoverage.BehindHead ? FuluSlot : GloasSlot))];

        bool available = await CreateSync(pool, discovery, clock: null, peers).FetchGloasColumnsByRootAsync(chain.GloasRoot, chain.Bid, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(available, Is.EqualTo(coverage != RootColumnCoverage.OneWithheld));
        if (coverage == RootColumnCoverage.HalfHeld)
        {
            Assert.That(requests[0], Has.Count.EqualTo(1));
            Assert.That(requests[0][0], Has.Length.EqualTo(1), "one identifier for the one block");
            Assert.That(requests[0][0][0].BlockRoot, Is.EqualTo(chain.GloasRoot));
            Assert.That(requests[0][0][0].Columns, Is.EquivalentTo(sampled.Skip(sampled.Length / 2)), "held columns are not requested again");
        }
        if (coverage is RootColumnCoverage.Missing or RootColumnCoverage.AllHeld)
            Assert.That(requests.Select(static r => r.Count), Is.EqualTo(coverage == RootColumnCoverage.AllHeld ? new[] { 0, 0 } : new[] { 1, 1 }));
        if (coverage == RootColumnCoverage.OneWithheld)
        {
            Assert.That(requests.Select(static r => r.Count), Is.EqualTo(new[] { 1, 1, 1, 0 }), "at most three peers are asked");
            Assert.That(pool.TryGetGloas(chain.GloasRoot, sampled[^1], out _), Is.False);
        }
        if (coverage != RootColumnCoverage.AllHeld)
            Assert.That(sampled.Where(c => coverage != RootColumnCoverage.OneWithheld || c != sampled[^1]).All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True);
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(available, Is.True);
        Assert.That(lying.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
        Assert.That(secondPeerRequests, Is.EqualTo(new[] { sampled }), "the peers are asked at once, each for every missing column");
        Assert.That(pool.TryGetGloas(chain.GloasRoot, victim, out DataColumnSidecarGloas? held) ? held : null, Is.Not.Null.And.Property(nameof(DataColumnSidecarGloas.Slot)).EqualTo(GloasSlot), "the honest sidecar is pooled");
        Assert.That(pool.TryGetGloas(OtherRoot, victim, out _), Is.False, "a sidecar for another block is not pooled");
        Assert.That(honest.Failures, Is.Zero);
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(available, Is.True);
        Assert.That(late.Reports, Is.Empty, "the tampered copies were never verified");
        Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out DataColumnSidecarGloas? held) && ReferenceEquals(held, served[c])), Is.True, "the first reply's sidecars stay pooled");
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(available, Is.True);
        Assert.That(failing.Reports, Is.EqualTo(new[] { PeerFailureReason.RequestFailed }));
        Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True);
        Assert.That(honest.Failures, Is.Zero);
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(available, Is.False);
        Assert.That(allAsked.MaxInFlight, Is.EqualTo(PeersPerFetch), "every peer of the fetch is asked before any answers");
        Assert.That(elapsed.Elapsed, Is.LessThan(requestTimeout), "no request waited out its timeout behind another");
        Assert.That(peers.Select(static p => p.Failures), Is.All.EqualTo(1));
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((first, second), Is.EqualTo((false, true)));
        Assert.That(requests, Is.EqualTo(new[] { 1, 1, 1, 1 }), "the second fetch asks only the peer the first did not");
        Assert.That(sampled.All(c => pool.TryGetGloas(chain.GloasRoot, c, out _)), Is.True);
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(available, Is.EqualTo(expected));
        Assert.That(requests, Is.Zero);
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
            case BadSidecar.MalformedColumn:
                sidecar.Column = [sidecar.Column![0]];
                break;
        }

        return sidecar;
    }

    private static ulong UnsampledColumn(ulong[] sampled) =>
        Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).First(c => !sampled.Contains(c));

    private static Task<List<ForkedSignedBeaconBlock>> RunAsync(RangeSyncTests.StubPeer peer, DataColumnSidecarPool pool, BeaconDiscovery discovery, SlotClock? clock, StraddlingChain chain, CancellationToken token) =>
        RunAsync(pool, discovery, clock, chain, GloasSlot, token, peer);

    private static async Task<List<ForkedSignedBeaconBlock>> RunAsync(DataColumnSidecarPool pool, BeaconDiscovery discovery, SlotClock? clock, StraddlingChain chain, ulong targetSlot, CancellationToken token, params IBeaconSyncPeer[] peers) =>
        await RangeSyncTests.CollectAsync(CreateSync(pool, discovery, clock, peers).Run(chain.AnchorRoot, chain.Anchor, () => targetSlot, token));

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
        public Hash256? SecondGloasRoot { get; init; }
        public ulong Anchor { get; init; } = AnchorSlot;

        public static StraddlingChain Create(ulong bidSlot = GloasSlot, bool withSecondGloas = false, ulong anchorSlot = AnchorSlot)
        {
            SignedBeaconBlock anchor = TestChain.CreateBlock(anchorSlot, Hash256.Zero);
            Hash256 anchorRoot = SszRoots.HashTreeRoot(anchor.Message!);
            SignedBeaconBlock fulu = TestChain.CreateBlock(FuluSlot, anchorRoot);
            fulu.Message!.Body!.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
            SignedBeaconBlockGloas gloas = CreateMinimalGloasBlock(GloasSlot, SszRoots.HashTreeRoot(fulu.Message));
            ExecutionPayloadBid bid = gloas.Message!.Body!.SignedExecutionPayloadBid!.Message!;
            bid.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
            bid.Slot = bidSlot;
            Hash256 gloasRoot = SszRoots.HashTreeRoot(gloas.Message);
            List<ForkedSignedBeaconBlock> blocks = [new ForkedSignedBeaconBlock.OfFulu(fulu), new ForkedSignedBeaconBlock.OfGloas(gloas)];
            Hash256? secondRoot = null;
            if (withSecondGloas)
            {
                SignedBeaconBlockGloas second = CreateMinimalGloasBlock(SecondGloasSlot, gloasRoot);
                ExecutionPayloadBid secondBid = second.Message!.Body!.SignedExecutionPayloadBid!.Message!;
                secondBid.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
                secondBid.Slot = SecondGloasSlot;
                secondRoot = SszRoots.HashTreeRoot(second.Message);
                blocks.Add(new ForkedSignedBeaconBlock.OfGloas(second));
            }

            return new StraddlingChain
            {
                AnchorRoot = anchorRoot,
                Blocks = [.. blocks],
                GloasRoot = gloasRoot,
                Bid = bid,
                SecondGloasRoot = secondRoot,
                Anchor = anchorSlot,
            };
        }

        public DataColumnSidecarGloas GloasSidecar(ulong column) => DataColumnSidecarGloasTestFixture.BuildSidecar(column, GloasSlot, GloasRoot);

        public DataColumnSidecarGloas SecondGloasSidecar(ulong column) => DataColumnSidecarGloasTestFixture.BuildSidecar(column, SecondGloasSlot, SecondGloasRoot!);

        public RangeSyncTests.StubPeer HonestPeer(string id, PeerColumnCustody? custody = null) =>
            CreatePeer((_, _, columns) => [.. columns.Select(GloasSidecar)], id: id, custody: custody);

        /// <summary>Serves <see cref="Blocks"/> by range and no Fulu sidecars, recording each Fulu column window.</summary>
        public RangeSyncTests.StubPeer CreatePeer(Func<ulong, ulong, ulong[], DataColumnSidecarGloas[]> gloasColumnHandler, List<(ulong StartSlot, ulong Count)>? fuluWindows = null, string id = "peer", PeerColumnCustody? custody = null, ulong earliestAvailableSlot = 0, ulong headSlot = GloasSlot) => new(
            id,
            headSlot,
            (startSlot, count) => [.. Blocks.Where(b => b.Slot >= startSlot && b.Slot - startSlot < count)],
            (startSlot, count, _) =>
            {
                fuluWindows?.Add((startSlot, count));
                return [];
            },
            gloasColumnHandler,
            custody: custody,
            earliestAvailableSlot: earliestAvailableSlot);

        public RangeSyncTests.StubPeer CreateRootPeer(string id, Func<DataColumnsByRootIdentifier[], DataColumnSidecarGloas[]> gloasRootHandler, ulong headSlot = GloasSlot) => new(
            id,
            headSlot,
            static (_, _) => [],
            gloasRootHandler: gloasRootHandler);
    }
}
