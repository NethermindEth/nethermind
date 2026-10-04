// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Api;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.RangeSyncTests;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// fulu/p2p-interface.md: a peer answers DataColumnSidecarsByRange and DataColumnSidecarsByRoot only with the columns it
/// custodies, and typical peers custody <c>CUSTODY_REQUIREMENT</c> groups, fewer than this node samples. Each sampled
/// column must therefore be asked of a peer custodying it, or the block never passes the availability gate.
/// </summary>
public class RangeSyncColumnCustodyTests
{
    /// <summary>Expected columns are pyspec <c>get_custody_groups</c> for the node id keccak(PrivateKeyA public key) at 8 groups, computed outside this code base.</summary>
    [Test]
    public async Task The_fixture_node_samples_the_columns_the_spec_assigns_to_its_fixed_identity()
    {
        await using Fixture fixture = Fixture.Create();

        Assert.That(fixture.Sampled, Is.EqualTo(new ulong[] { 15, 35, 42, 45, 96, 105, 115, 120 }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_peer_is_never_asked_for_a_column_it_does_not_custody(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ulong[] sampled = fixture.Sampled;
        ulong[] unsampled = [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).Except(sampled)];
        // The block peer comes first in the pool and custodies only half of the sample.
        StubPeer[] peers =
        [
            fixture.Peer("half", [.. sampled[..(sampled.Length / 2)], .. unsampled[..4]]),
            fixture.Peer("rest", sampled[(sampled.Length / 2)..]),
            fixture.Peer("unsampled", unsampled[4..8]),
            fixture.Peer("unknown", custody: PeerColumnCustody.None),
        ];

        await fixture.RunOneRoundAsync(peers, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        foreach (StubPeer peer in peers)
        {
            Assert.That(peer.RequestedColumns.SelectMany(static c => c), Is.All.Matches<ulong>(peer.Custody.Custodies), $"{peer.Id} is asked only for columns it custodies");
        }

        Assert.That(peers.SelectMany(static p => p.RequestedColumns).SelectMany(static c => c), Is.EquivalentTo(sampled), "every sampled column is asked of exactly one custodian");
        Assert.That(peers[2].ColumnRequests + peers[3].ColumnRequests, Is.Zero, "a peer custodying no missing column gets no request");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_batch_whose_sample_is_spread_over_partial_custody_peers_becomes_available_in_one_round(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ulong[] sampled = fixture.Sampled;
        StubPeer[] peers =
        [
            fixture.Peer("a", [.. sampled.Where(static (_, i) => i % 3 == 0)]),
            fixture.Peer("b", [.. sampled.Where(static (_, i) => i % 3 == 1)]),
            fixture.Peer("c", [.. sampled.Where(static (_, i) => i % 3 == 2)]),
        ];

        IReadOnlyList<ForkedSignedBeaconBlock> yielded = await fixture.RunOneRoundAsync(peers, token);

        Assert.That(yielded.Select(b => fixture.Importer.Import(b, fixture.Chain.BlockRoot, verifySignatures: true)), Is.EqualTo(new[] { BlockImportResult.Imported }));
    }

    [Test]
    public void Columns_go_to_advertised_custodians_first_and_to_a_bounded_number_of_peers()
    {
        StubPeer floor = PeerWithCustody("floor", new PeerColumnCustody([0, 1], isAdvertised: false));
        StubPeer advertised = PeerWithCustody("advertised", new PeerColumnCustody([1], isAdvertised: true));
        StubPeer[] singles = [.. Enumerable.Range(2, 4).Select(c => PeerWithCustody($"single-{c}", new PeerColumnCustody([(ulong)c], isAdvertised: true)))];

        // Column 1 comes first, while both its custodians are equally loaded and the floor peer is earlier in the pool.
        List<(IBeaconSyncPeer Peer, ulong[] Columns)> requests = RangeSync.AssignColumns([1, 0, 2, 3, 4, 5], [floor, advertised, .. singles], maxPeers: 4);

        Assert.That(requests.Select(static r => (r.Peer.Id, r.Columns)), Is.EqualTo(new (string, ulong[])[]
        {
            ("advertised", [1]),
            ("floor", [0]),
            ("single-2", [2]),
            ("single-3", [3]),
        }), "column 1 goes to its advertised custodian; columns 4 and 5 would need a fifth peer");
    }

    /// <summary>
    /// A Fulu block deferred for missing columns is only re-checked on slot ticks, so without a by-root fetch a column no
    /// range response carried stalls the node until finality passes the block.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_block_deferred_for_missing_columns_recovers_by_root_from_a_custodying_peer_once_per_slot(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        bool serving = false;
        // Advertised and first in the pool, so it would be asked first were custody ignored.
        StubPeer bystander = fixture.Peer("bystander", [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).Except(fixture.Sampled)]);
        StubPeer custodian = new(
            "custodian",
            fixture.Chain.Block.Message!.Slot,
            static (_, _) => [],
            custody: new PeerColumnCustody(fixture.Sampled, isAdvertised: true),
            rootHandler: identifiers => serving ? fixture.ServeColumns(identifiers.Single().Columns!) : []);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator(bystander, custodian);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);

        BlockImportResult first = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        serving = true;
        BlockImportResult sameSlot = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        int requestsBeforeTick = custodian.RootColumnRequests;
        fixture.AdvanceSlots(1);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        await orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((first, sameSlot), Is.EqualTo((BlockImportResult.DataUnavailable, BlockImportResult.DataUnavailable)));
        Assert.That(requestsBeforeTick, Is.EqualTo(1), "the second deferral in the same slot does not fetch again");
        Assert.That(custodian.RootColumnRequests, Is.EqualTo(2), "the slot tick's retry fetches again");
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the retry imports the block once its columns arrived");
        Assert.That(bystander.RootColumnRequests, Is.Zero, "a peer custodying no sampled column is never asked");
    }

    /// <summary>An empty custodian set is a custody shortfall, not an earliest_available_slot problem, so the log names the columns and zero custodians.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_fetch_with_no_custodian_logs_the_custody_shortfall_not_the_earliest_available_slot([Values] bool byRoot, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer bystander = fixture.Peer("bystander", [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).Except(fixture.Sampled)]);
        TestLogRecorder log = new();
        RangeSync sync = new(new StubPool(bystander), new OneLoggerLogManager(new ILogger(log)), fixture.SidecarPool, fixture.Chain.Spec, fixture.Clock, fixture.Discovery);

        if (byRoot)
        {
            await sync.FetchColumnsByRootAsync(fixture.Chain.BlockRoot, fixture.Chain.Block.Message!, token);
        }
        else
        {
            await DrainAsync(sync.Run(fixture.Chain.AnchorRoot, fixture.Chain.AnchorBlock.Message!.Slot, () => fixture.Chain.Block.Message!.Slot, token));
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(log.Messages, Has.Some.Contains("0 custodians").And.Contains(fixture.Sampled[0].ToString()), "the shortfall names the missing columns");
        Assert.That(log.Messages, Has.None.Contains("serve from"), "the earliest available slot is not what is wrong");
        Assert.That(log.Messages, Has.None.Contains("Exception"));
        Assert.That(bystander.ColumnRequests + bystander.RootColumnRequests, Is.Zero);
    }

    /// <summary>A reply cut short keeps the reason it failed: a closed session must not read as a failed request, or the peer stays on a failure budget it can never work off.</summary>
    [TestCase(false, PeerFailureReason.RequestFailed)]
    [TestCase(true, PeerFailureReason.SessionClosed)]
    [CancelAfter(30_000)]
    public async Task A_reply_cut_short_is_reported_under_the_reason_it_failed(bool sessionClosed, PeerFailureReason expected, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        Exception cause = sessionClosed
            ? new System.IO.IOException("peer disconnected after 1 s: its libp2p session closed", new OperationCanceledException())
            : new TimeoutException("request timed out");
        StubPeer cutShort = fixture.FailingColumnPeer("cut-short", columns => throw new PartialSidecarsException(cause, fixture.ServeColumns(columns[..1])));
        StubPeer good = fixture.Peer("good", custody: StubPeer.AllColumns);

        await fixture.RunOneRoundAsync([cutShort, good], token);

        Assert.That(cutShort.Reports, Is.EqualTo(new[] { expected }));
    }

    /// <summary>The importer defers a block whose columns are missing and fetches them by root, so a failed column batch must not hold its blocks back.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_batch_whose_column_requests_all_fail_still_yields_its_blocks_for_the_by_root_fetch(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer failing = fixture.FailingColumnPeer("failing", static _ => throw new TimeoutException("request timed out"));

        IReadOnlyList<ForkedSignedBeaconBlock> yielded = await fixture.RunOneRoundAsync([failing], token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(yielded.Select(static b => b.Slot), Is.EqualTo(new[] { fixture.Chain.Block.Message!.Slot }));
        Assert.That(failing.ColumnRequests, Is.EqualTo(1), "the lone custodian is not asked again");
        Assert.That(fixture.Importer.Import(yielded[0], fixture.Chain.BlockRoot, verifySignatures: true), Is.EqualTo(BlockImportResult.DataUnavailable));
    }

    /// <summary>A failed column request is logged for the operator, who searches logs for exceptions: the line names the cause's text, never a type name.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_failed_column_request_is_logged_with_the_sidecars_kept_and_without_exception_type_names(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer cutShort = fixture.FailingColumnPeer("cut-short", columns => throw new PartialSidecarsException(new TimeoutException("request timed out"), fixture.ServeColumns(columns[..1])));
        TestLogRecorder log = new();
        RangeSync sync = new(new StubPool(cutShort), new OneLoggerLogManager(new ILogger(log)), fixture.SidecarPool, fixture.Chain.Spec, fixture.Clock, fixture.Discovery);

        await DrainAsync(sync.Run(fixture.Chain.AnchorRoot, fixture.Chain.AnchorBlock.Message!.Slot, () => fixture.Chain.Block.Message!.Slot, token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(log.Messages, Has.Some.Matches<string>(static line => line.Contains("cut-short failed after") && line.Contains("keeping 1 sidecars read")));
        Assert.That(log.Messages, Has.None.Contains("Exception"));
    }

    /// <summary>A later round must ask only for the slots still lacking a column, not the whole batch window again.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_later_round_requests_only_from_the_first_slot_still_missing_a_column(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconBlock first = fixture.Chain.Block.Message!;
        BeaconBlock second = new() { Slot = first.Slot + 1, ProposerIndex = first.ProposerIndex, ParentRoot = fixture.Chain.BlockRoot, StateRoot = first.StateRoot, Body = first.Body };
        Hash256 secondRoot = SszRoots.HashTreeRoot(second);
        ForkedSignedBeaconBlock[] blocks = [new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = second, Signature = default })];
        ulong lackingColumn = fixture.Sampled[0];
        foreach (ulong column in fixture.Sampled)
        {
            fixture.SidecarPool.Add(fixture.Chain.BlockRoot, first.Slot, fixture.Chain.Columns[(int)column]);
            if (column != lackingColumn)
            {
                fixture.SidecarPool.Add(secondRoot, second.Slot, fixture.Chain.Columns[(int)column]);
            }
        }

        List<(ulong Start, ulong Count)> requests = [];
        StubPeer[] peers = [.. Enumerable.Range(0, 2).Select(i => new StubPeer($"peer-{i}", second.Slot, (_, _) => blocks, (start, count, _) =>
        {
            lock (requests)
            {
                requests.Add((start, count));
            }

            return [];
        }))];

        await DrainAsync(fixture.CreateRangeSync(peers).Run(fixture.Chain.AnchorRoot, fixture.Chain.AnchorBlock.Message!.Slot, () => second.Slot, token));

        Assert.That(requests, Is.EqualTo(new[] { (first.Slot, 2UL), (second.Slot, 1UL) }), "round 0 covers the batch, the next round only the slot still lacking the column");
    }

    /// <summary>
    /// A later round's first missing slot can lie past every peer's last status although the batch's blocks came from those peers (phase0/p2p-interface.md Status),
    /// so when no custodian's status reaches that slot one reaching the batch start is asked, as the blocks were; otherwise the column is never asked for.
    /// A custodian whose status reaches the slot is still asked first: one behind it may answer the range empty, which by-range allows, and waste the round.
    /// </summary>
    [TestCase(false, TestName = "A later round asks a custodian whose status reaches the batch start when none reaches the slot missing a column")]
    [TestCase(true, TestName = "A later round asks a custodian whose status reaches the slot missing a column before one reaching only the batch start")]
    [CancelAfter(30_000)]
    public async Task A_later_round_seeks_custodians_reaching_the_missing_slot_then_the_batch_start(bool custodianReachingTheSlot, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconBlock first = fixture.Chain.Block.Message!;
        (SignedBeaconBlock second, Hash256 secondRoot, DataColumnSidecar[] secondColumns) = ImportableBlobBlock.BlobBlockAt(first.Slot + 1, fixture.Chain.BlockRoot);
        ForkedSignedBeaconBlock[] blocks = [new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), new ForkedSignedBeaconBlock.OfFulu(second)];
        ulong lackingColumn = fixture.Sampled[0];
        foreach (ulong column in fixture.Sampled)
        {
            fixture.SidecarPool.Add(fixture.Chain.BlockRoot, first.Slot, fixture.Chain.Columns[(int)column]);
            if (column != lackingColumn)
            {
                fixture.SidecarPool.Add(secondRoot, second.Message!.Slot, secondColumns[(int)column]);
            }
        }

        Func<ulong, ulong, ulong[], DataColumnSidecar[]> serveSecond = (_, _, columns) => [.. columns.Select(c => secondColumns[(int)c])];
        StubPeer blockSource = new("blocks", second.Message!.Slot, (_, _) => blocks, custody: PeerColumnCustody.None);
        // Advertised and listed first, so round 0 prefers it; it leaves the column unserved and is not asked again.
        StubPeer unserving = new("unserving", second.Message.Slot, (_, _) => blocks, static (_, _, _) => [], custody: StubPeer.AllColumns);
        // Advertised, so custody alone would rank it above the custodian reaching the slot.
        StubPeer stale = new("stale", first.Slot, (_, _) => blocks, serveSecond, custody: new PeerColumnCustody(fixture.Sampled, isAdvertised: true));
        StubPeer reaching = new("reaching", second.Message.Slot, (_, _) => blocks, serveSecond, custody: new PeerColumnCustody(fixture.Sampled, isAdvertised: false));
        IBeaconSyncPeer[] peers = custodianReachingTheSlot ? [blockSource, unserving, stale, reaching] : [blockSource, unserving, stale];

        await DrainAsync(fixture.CreateRangeSync(peers).Run(fixture.Chain.AnchorRoot, fixture.Chain.AnchorBlock.Message!.Slot, () => second.Message.Slot, token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(unserving.ColumnRequests, Is.EqualTo(1), "fixture: round 0 asked the advertised custodian listed first");
        Assert.That((stale.ColumnRequests, reaching.ColumnRequests), Is.EqualTo(custodianReachingTheSlot ? (0, 1) : (1, 0)));
        Assert.That(fixture.SidecarPool.TryGet(secondRoot, lackingColumn, out _), Is.True);
    }

    /// <summary>
    /// The preference for custodians reaching the missing slot is per column: a column only a custodian reaching just the batch start holds is asked for in the same round
    /// as the columns the others hold, not left to a later one, which <see cref="RangeSync"/> may not have.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_later_round_asks_a_column_no_custodian_reaching_the_missing_slot_holds_in_the_same_round_from_one_reaching_the_batch_start(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconBlock first = fixture.Chain.Block.Message!;
        (SignedBeaconBlock second, Hash256 secondRoot, DataColumnSidecar[] secondColumns) = ImportableBlobBlock.BlobBlockAt(first.Slot + 1, fixture.Chain.BlockRoot);
        ForkedSignedBeaconBlock[] blocks = [new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), new ForkedSignedBeaconBlock.OfFulu(second)];
        ulong reachedColumn = fixture.Sampled[0];
        ulong behindColumn = fixture.Sampled[1];
        foreach (ulong column in fixture.Sampled)
        {
            fixture.SidecarPool.Add(fixture.Chain.BlockRoot, first.Slot, fixture.Chain.Columns[(int)column]);
            if (column != reachedColumn && column != behindColumn)
            {
                fixture.SidecarPool.Add(secondRoot, second.Message!.Slot, secondColumns[(int)column]);
            }
        }

        Func<ulong, ulong, ulong[], DataColumnSidecar[]> serveSecond = (_, _, columns) => [.. columns.Select(c => secondColumns[(int)c])];
        StubPeer blockSource = new("blocks", second.Message!.Slot, (_, _) => blocks, custody: PeerColumnCustody.None);
        // The only advertised custodian, so round 0 gives it both columns; it leaves them unserved and is not asked again.
        StubPeer unserving = new("unserving", second.Message.Slot, (_, _) => blocks, static (_, _, _) => [], custody: StubPeer.AllColumns);
        StubPeer reaching = new("reaching", second.Message.Slot, (_, _) => blocks, serveSecond, custody: new PeerColumnCustody([reachedColumn], isAdvertised: false));
        bool? reachedColumnHeldWhenStaleAsked = null;
        StubPeer stale = new("stale", first.Slot, (_, _) => blocks, (start, count, columns) =>
        {
            reachedColumnHeldWhenStaleAsked = fixture.SidecarPool.TryGet(secondRoot, reachedColumn, out _);
            return serveSecond(start, count, columns);
        }, custody: new PeerColumnCustody([behindColumn], isAdvertised: false));

        await DrainAsync(fixture.CreateRangeSync([blockSource, unserving, reaching, stale]).Run(fixture.Chain.AnchorRoot, fixture.Chain.AnchorBlock.Message!.Slot, () => second.Message.Slot, token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(unserving.ColumnRequests, Is.EqualTo(1), "fixture: round 0 asked the advertised custodian for both columns");
        Assert.That((reaching.ColumnRequests, stale.ColumnRequests), Is.EqualTo((1, 1)));
        // Replies are pooled once the whole round has answered, so a column still missing shows the two were asked in one round.
        Assert.That(reachedColumnHeldWhenStaleAsked, Is.False);
        Assert.That(fixture.SidecarPool.TryGet(secondRoot, reachedColumn, out _) && fixture.SidecarPool.TryGet(secondRoot, behindColumn, out _), Is.True);
    }

    /// <summary>A reply repeating one invalid (root, index) is verified and penalized once, not once per copy: each copy would cost a KZG verification.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Repeated_invalid_copies_of_a_column_in_one_reply_penalize_the_peer_once([Values] bool malformedCopyFirst, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ulong? firstColumn = null;
        StubPeer peer = fixture.FailingColumnPeer("repeating", columns =>
        {
            firstColumn ??= columns[0];
            DataColumnSidecar valid = fixture.Chain.Columns[(int)columns[0]];
            DataColumnSidecar tampered = new()
            {
                Index = valid.Index,
                Column = [.. valid.Column!.Select(static cell => { byte[] bytes = cell.AsSpan().ToArray(); bytes[^1] ^= 0xFF; return SszBlobCell.FromSpan(bytes); })],
                KzgCommitments = valid.KzgCommitments,
                KzgProofs = valid.KzgProofs,
                SignedBlockHeader = valid.SignedBlockHeader,
                KzgCommitmentsInclusionProof = valid.KzgCommitmentsInclusionProof,
            };
            DataColumnSidecar truncated = new()
            {
                Index = valid.Index,
                Column = valid.Column![..^1],
                KzgCommitments = valid.KzgCommitments,
                KzgProofs = valid.KzgProofs,
                SignedBlockHeader = valid.SignedBlockHeader,
                KzgCommitmentsInclusionProof = valid.KzgCommitmentsInclusionProof,
            };
            return [.. fixture.ServeColumns(columns[1..]), .. Enumerable.Repeat(truncated, malformedCopyFirst ? 1 : 0), .. Enumerable.Repeat(tampered, 50), valid];
        });

        await fixture.RunOneRoundAsync([peer], token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
        Assert.That(fixture.SidecarPool.TryGet(fixture.Chain.BlockRoot, firstColumn!.Value, out _), Is.False, "a KZG failure hides later copies in this reply");
    }

    /// <summary>Only a KZG failure shadows later copies; fulu/p2p-interface.md (v1.7.0-beta.2) separates structure, inclusion and KZG verification.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_range_copy_failing_a_cheap_check_does_not_hide_the_valid_copy_after_it([Range(0, 2)] int invalidPart, [Values(1, 3)] int malformedCopies, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ulong? firstColumn = null;
        StubPeer peer = fixture.FailingColumnPeer("malformed-then-valid", columns =>
        {
            firstColumn ??= columns[0];
            DataColumnSidecar valid = fixture.Chain.Columns[(int)columns[0]];
            DataColumnSidecar malformed = new()
            {
                Index = valid.Index,
                Column = invalidPart == 0 ? valid.Column![..^1] : valid.Column,
                KzgCommitments = invalidPart == 2 ? valid.KzgCommitments![..^1] : valid.KzgCommitments,
                KzgProofs = valid.KzgProofs,
                SignedBlockHeader = valid.SignedBlockHeader,
                KzgCommitmentsInclusionProof = invalidPart == 1 ? [.. valid.KzgCommitmentsInclusionProof!.Select(static _ => Hash256.Zero)] : valid.KzgCommitmentsInclusionProof,
            };
            return [.. Enumerable.Repeat(malformed, malformedCopies), .. fixture.ServeColumns(columns)];
        });

        await fixture.RunOneRoundAsync([peer], token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fixture.SidecarPool.TryGet(fixture.Chain.BlockRoot, firstColumn!.Value, out _), Is.True, "the valid copy behind the malformed one is pooled");
        Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }), "the malformed copies are penalized once");
    }

    /// <summary>Columns must obey their epoch blob limit (v1.7.0-beta.2 fulu/p2p-interface.md, verify_data_column_sidecar).</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_range_column_over_the_blob_limit_of_its_epoch_is_not_pooled(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconChainSpec chainSpec = fixture.Chain.Spec;
        BeaconChainSpec oneBlobSpec = new()
        {
            ChainId = chainSpec.ChainId,
            CheckpointSyncUrl = chainSpec.CheckpointSyncUrl,
            Bootnodes = chainSpec.Bootnodes,
            SecondsPerSlot = chainSpec.SecondsPerSlot,
            SlotsPerEpoch = chainSpec.SlotsPerEpoch,
            GenesisTime = chainSpec.GenesisTime,
            GenesisValidatorsRoot = chainSpec.GenesisValidatorsRoot,
            Forks = chainSpec.Forks,
            BlobSchedule = [],
            ElectraForkEpoch = chainSpec.ElectraForkEpoch,
            FuluForkEpoch = chainSpec.FuluForkEpoch,
            MaxBlobsPerBlockElectra = (ulong)fixture.Chain.Block.Message!.Body!.BlobKzgCommitments!.Length - 1,
            GloasForkEpoch = chainSpec.GloasForkEpoch,
            GloasForkVersion = chainSpec.GloasForkVersion,
        };
        ulong[] served = [];
        StubPeer peer = fixture.FailingColumnPeer("over-limit", columns =>
        {
            served = columns;
            return [.. fixture.ServeColumns(columns), .. fixture.ServeColumns(columns)];
        });
        RangeSync sync = new(new StubPool(peer), LimboLogs.Instance, fixture.SidecarPool, oneBlobSpec, fixture.Clock, fixture.Discovery);

        await DrainAsync(sync.Run(fixture.Chain.AnchorRoot, fixture.Chain.AnchorBlock.Message!.Slot, () => fixture.Chain.Block.Message!.Slot, token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(served, Is.Not.Empty, "fixture: the peer was asked for columns");
        Assert.That(served, Is.All.Matches<ulong>(column => !fixture.SidecarPool.TryGet(fixture.Chain.BlockRoot, column, out _)), "no column over the blob limit is pooled");
        Assert.That(peer.Reports, Is.EqualTo(Enumerable.Repeat(PeerFailureReason.ProtocolViolation, served.Length)), "each (root, index) is penalized once, not once per copy");
    }

    /// <summary>An unrequested sidecar earns one penalty per key and cannot hide requested data (v1.7.0-beta.2 fulu/p2p-interface.md, DataColumnSidecarsByRange).</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_range_sidecar_for_an_unrequested_block_is_penalized_once_and_hides_nothing(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ulong? firstColumn = null;
        StubPeer peer = fixture.FailingColumnPeer("other-block", columns =>
        {
            firstColumn ??= columns[0];
            DataColumnSidecar valid = fixture.Chain.Columns[(int)columns[0]];
            BeaconBlockHeader header = valid.SignedBlockHeader!.Message!;
            DataColumnSidecar other = new()
            {
                Index = valid.Index,
                SignedBlockHeader = new SignedBeaconBlockHeader
                {
                    Message = new BeaconBlockHeader { Slot = header.Slot + 1000, ProposerIndex = header.ProposerIndex, ParentRoot = header.ParentRoot, StateRoot = header.StateRoot, BodyRoot = header.BodyRoot },
                },
            };
            return [other, other, .. fixture.ServeColumns(columns)];
        });

        await fixture.RunOneRoundAsync([peer], token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fixture.SidecarPool.TryGet(fixture.Chain.BlockRoot, firstColumn!.Value, out _), Is.True, "the requested block's column is pooled");
        Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }), "the repeated unrequested sidecar is penalized once");
    }

    /// <summary>One supernode must not take a whole batch while other custodians can serve columns: it is asked for at most the per-peer bound, the rest wait for the next round.</summary>
    [Test]
    public void A_peer_is_asked_for_at_most_the_per_peer_bound_of_columns()
    {
        StubPeer supernode = PeerWithCustody("supernode", StubPeer.AllColumns);
        StubPeer other = PeerWithCustody("other", new PeerColumnCustody([1], isAdvertised: true));

        List<(IBeaconSyncPeer Peer, ulong[] Columns)> requests = RangeSync.AssignColumns([0, 1, 2, 3, 4, 5], [supernode, other], maxPeers: 2, maxColumnsPerPeer: 2);

        Assert.That(requests.Select(static r => (r.Peer.Id, r.Columns)), Is.EqualTo(new (string, ulong[])[]
        {
            ("supernode", [0, 2]),
            ("other", [1]),
        }), "columns beyond the bound are left for a later round");
    }

    private static StubPeer PeerWithCustody(string id, PeerColumnCustody custody) => new(id, headSlot: 0, static (_, _) => [], custody: custody);

    internal sealed class Fixture(DeferredBlockColumnFetchTests.Fixture fixture) : IAsyncDisposable
    {
        public ImportableBlobBlock Chain => fixture.Chain;
        public DataColumnSidecarPool SidecarPool => fixture.SidecarPool;
        public BeaconDiscovery Discovery => fixture.Discovery;
        public SlotClock Clock => fixture.Clock;
        public IBlockImporter Importer => fixture.Importer;
        public ulong[] Sampled => fixture.Sampled;

        public static Fixture Create() => new(DeferredBlockColumnFetchTests.Fixture.Create(identity: TestItem.PrivateKeyA.KeyBytes));

        public void AdvanceSlots(ulong slots) => fixture.AdvanceSlots(slots);

        /// <summary>An honest peer: it serves the chain's block, and of the columns asked only those it custodies.</summary>
        public StubPeer Peer(string id, ulong[]? custodied = null, PeerColumnCustody? custody = null)
        {
            PeerColumnCustody peerCustody = custody ?? new PeerColumnCustody(custodied!, isAdvertised: true);
            return new StubPeer(
                id,
                Chain.Block.Message!.Slot,
                (_, _) => [new ForkedSignedBeaconBlock.OfFulu(Chain.Block)],
                (_, _, columns) => ServeColumns([.. columns.Where(peerCustody.Custodies)]),
                custody: peerCustody,
                rootHandler: identifiers => ServeColumns([.. identifiers.Single().Columns!.Where(peerCustody.Custodies)]));
        }

        /// <summary>A supernode that serves the chain's block and answers every by-range column request with <paramref name="onColumnRequest"/>, which normally throws.</summary>
        public StubPeer FailingColumnPeer(string id, Func<ulong[], DataColumnSidecar[]> onColumnRequest) =>
            new(
                id,
                Chain.Block.Message!.Slot,
                (_, _) => [new ForkedSignedBeaconBlock.OfFulu(Chain.Block)],
                (_, _, columns) => onColumnRequest(columns),
                custody: StubPeer.AllColumns);

        public DataColumnSidecar[] ServeColumns(ulong[] columns) => [.. columns.Select(c => Chain.Columns[(int)c])];

        public RangeSync CreateRangeSync(IBeaconSyncPeer[] peers) =>
            new(new StubPool(peers), LimboLogs.Instance, SidecarPool, Chain.Spec, Clock, Discovery);

        public async Task<IReadOnlyList<ForkedSignedBeaconBlock>> RunOneRoundAsync(IBeaconSyncPeer[] peers, CancellationToken token) =>
            await CollectAsync(CreateRangeSync(peers).Run(Chain.AnchorRoot, Chain.AnchorBlock.Message!.Slot, () => Chain.Block.Message!.Slot, token));

        public BeaconSyncOrchestrator CreateOrchestrator(params IBeaconSyncPeer[] peers)
        {
            StubPool pool = new(peers);
            BeaconSyncOrchestrator orchestrator = new(
                new BeaconChainConfig(),
                Chain.Spec,
                fixture.Store,
                new BlockImporterFactory(Chain.Spec, fixture.Store, Chain.Pubkeys, new NoOpEngineDriver(), new BeaconChainConfig(), LimboLogs.Instance, SidecarPool, Clock, Discovery),
                new NoOpEngineDriver(),
                pool,
                CreateRangeSync(peers),
                Clock,
                new GossipRouter(Chain.Spec, Clock, LimboLogs.Instance),
                new BeaconChainStatusHolder(Chain.Spec, Timestamper.Default),
                LimboLogs.Instance)
            {
                // Gossip needs a started libp2p host, which is not what these tests are about.
                GossipStarted = true,
            };
            orchestrator.Initialize(Importer, new ForkedSignedBeaconBlock.OfFulu(Chain.AnchorBlock), Chain.AnchorRoot);
            return orchestrator;
        }

        public ValueTask DisposeAsync() => fixture.DisposeAsync();
    }
}
