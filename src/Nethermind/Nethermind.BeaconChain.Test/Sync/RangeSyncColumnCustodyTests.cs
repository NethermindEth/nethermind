// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Api;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
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

        using (Assert.EnterMultipleScope())
        {
            foreach (StubPeer peer in peers)
            {
                Assert.That(peer.RequestedColumns.SelectMany(static c => c), Is.All.Matches<ulong>(peer.Custody.Custodies), $"{peer.Id} is asked only for columns it custodies");
            }

            Assert.That(peers.SelectMany(static p => p.RequestedColumns).SelectMany(static c => c), Is.EquivalentTo(sampled), "every sampled column is asked of exactly one custodian");
            Assert.That(peers[2].ColumnRequests + peers[3].ColumnRequests, Is.Zero, "a peer custodying no missing column gets no request");
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That((first, sameSlot), Is.EqualTo((BlockImportResult.DataUnavailable, BlockImportResult.DataUnavailable)));
            Assert.That(requestsBeforeTick, Is.EqualTo(1), "the second deferral in the same slot does not fetch again");
            Assert.That(custodian.RootColumnRequests, Is.EqualTo(2), "the slot tick's retry fetches again");
            Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the retry imports the block once its columns arrived");
            Assert.That(bystander.RootColumnRequests, Is.Zero, "a peer custodying no sampled column is never asked");
        }
    }

    /// <summary>An empty custodian set is a custody shortfall, not an earliest_available_slot problem, so the log names the columns and zero custodians.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_fetch_with_no_custodian_logs_the_custody_shortfall_not_the_earliest_available_slot([Values] bool byRoot, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer bystander = fixture.Peer("bystander", [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).Except(fixture.Sampled)]);
        RangeSyncPeerSelectionTests.AllLevelsCapture log = new();
        RangeSync sync = new(new StubPool(bystander), new OneLoggerLogManager(new ILogger(log)), fixture.SidecarPool, fixture.Chain.Spec, fixture.Clock, fixture.Discovery);

        if (byRoot)
        {
            await sync.FetchColumnsByRootAsync(fixture.Chain.BlockRoot, fixture.Chain.Block.Message!, token);
        }
        else
        {
            await foreach (ForkedSignedBeaconBlock _ in sync.Run(fixture.Chain.AnchorRoot, fixture.Chain.AnchorBlock.Message!.Slot, () => fixture.Chain.Block.Message!.Slot, token))
            {
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.Lines, Has.Some.Contains("0 custodians").And.Contains(fixture.Sampled[0].ToString()), "the shortfall names the missing columns");
            Assert.That(log.Lines, Has.None.Contains("serve from"), "the earliest available slot is not what is wrong");
            Assert.That(log.Lines, Has.None.Contains("Exception"));
            Assert.That(bystander.ColumnRequests + bystander.RootColumnRequests, Is.Zero);
        }
    }

    /// <summary>
    /// A supernode that fails every by-range column request used to be asked again next batch and could not be told from a
    /// working one, so the batch's columns stayed missing; the columns must come from a custodian that has not failed.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_custodian_that_failed_a_column_request_is_not_asked_again_and_another_serves_the_columns(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer failing = fixture.FailingColumnPeer("failing", static _ => throw new Eth2ReqRespException("Truncated response chunk: Unable to read beyond the end of the stream."));
        StubPeer good = fixture.Peer("good", custody: StubPeer.AllColumns);

        IReadOnlyList<ForkedSignedBeaconBlock> yielded = await fixture.RunOneRoundAsync([failing, good], token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failing.ColumnRequests, Is.EqualTo(1), "a custodian that failed this batch is not picked again in it");
            Assert.That(failing.Reports, Is.EqualTo(new[] { PeerFailureReason.RequestFailed }));
            Assert.That(yielded.Select(b => fixture.Importer.Import(b, fixture.Chain.BlockRoot, verifySignatures: true)), Is.EqualTo(new[] { BlockImportResult.Imported }));
        }
    }

    /// <summary>A reply cut short after some chunks must keep them: only what is still missing is requested, from a peer that has not failed.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_reply_cut_short_keeps_its_chunks_and_only_the_rest_is_requested(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer cutShort = fixture.FailingColumnPeer("cut-short", columns => throw new PartialSidecarsException(
            new Eth2ReqRespException("Truncated response chunk: Unable to read beyond the end of the stream."),
            fixture.ServeColumns(columns[..1])));
        StubPeer good = fixture.Peer("good", custody: StubPeer.AllColumns);

        IReadOnlyList<ForkedSignedBeaconBlock> yielded = await fixture.RunOneRoundAsync([cutShort, good], token);

        ulong[] asked = cutShort.RequestedColumns.Single();
        ulong[] undelivered = asked[1..];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(good.RequestedColumns.Skip(1).SelectMany(static c => c), Is.EquivalentTo(undelivered), "the retry asks only for the columns the cut reply did not deliver");
            Assert.That(good.RequestedColumns.SelectMany(static c => c), Has.None.EqualTo(asked[0]), "the delivered column is not asked again");
            Assert.That(yielded.Select(b => fixture.Importer.Import(b, fixture.Chain.BlockRoot, verifySignatures: true)), Is.EqualTo(new[] { BlockImportResult.Imported }));
        }
    }

    /// <summary>The importer defers a block whose columns are missing and fetches them by root, so a failed column batch must not hold its blocks back.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_batch_whose_column_requests_all_fail_still_yields_its_blocks_for_the_by_root_fetch(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer failing = fixture.FailingColumnPeer("failing", static _ => throw new TimeoutException("request timed out"));

        IReadOnlyList<ForkedSignedBeaconBlock> yielded = await fixture.RunOneRoundAsync([failing], token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(yielded.Select(static b => b.Slot), Is.EqualTo(new[] { fixture.Chain.Block.Message!.Slot }));
            Assert.That(failing.ColumnRequests, Is.EqualTo(1), "the lone custodian is not asked again");
            Assert.That(fixture.Importer.Import(yielded[0], fixture.Chain.BlockRoot, verifySignatures: true), Is.EqualTo(BlockImportResult.DataUnavailable));
        }
    }

    /// <summary>A failed column request is logged for the operator, who searches logs for exceptions: the line names the cause's text, never a type name.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_failed_column_request_is_logged_with_the_sidecars_kept_and_without_exception_type_names(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer cutShort = fixture.FailingColumnPeer("cut-short", columns => throw new PartialSidecarsException(new TimeoutException("request timed out"), fixture.ServeColumns(columns[..1])));
        RangeSyncPeerSelectionTests.AllLevelsCapture log = new();
        RangeSync sync = new(new StubPool(cutShort), new OneLoggerLogManager(new ILogger(log)), fixture.SidecarPool, fixture.Chain.Spec, fixture.Clock, fixture.Discovery);

        await foreach (ForkedSignedBeaconBlock _ in sync.Run(fixture.Chain.AnchorRoot, fixture.Chain.AnchorBlock.Message!.Slot, () => fixture.Chain.Block.Message!.Slot, token))
        {
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.Lines, Has.Some.Matches<string>(static line => line.Contains("cut-short failed after") && line.Contains("keeping 1 sidecars read")));
            Assert.That(log.Lines, Has.None.Contains("Exception"));
        }
    }

    /// <summary>The advertised supernode is preferred by custody, so without the per-peer bound it takes every sampled column while the floor custodians idle.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task An_advertised_supernode_is_asked_for_no_more_than_its_share_while_other_custodians_serve_the_rest(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ulong[] sampled = fixture.Sampled;
        StubPeer supernode = fixture.Peer("supernode", custody: StubPeer.AllColumns);
        StubPeer[] floor = [.. Enumerable.Range(0, 3).Select(i => fixture.Peer($"floor-{i}", custody: new PeerColumnCustody([.. sampled.Where((_, c) => c % 3 == i)], isAdvertised: false)))];

        IReadOnlyList<ForkedSignedBeaconBlock> yielded = await fixture.RunOneRoundAsync([supernode, .. floor], token);

        int share = Math.Max(2, (sampled.Length + 3) / 4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(supernode.RequestedColumns.SelectMany(static c => c).Count(), Is.LessThanOrEqualTo(share), "one supernode does not take the batch");
            Assert.That(floor.Sum(static p => p.ColumnRequests), Is.GreaterThan(0), "the other custodians are used");
            Assert.That(yielded.Select(b => fixture.Importer.Import(b, fixture.Chain.BlockRoot, verifySignatures: true)), Is.EqualTo(new[] { BlockImportResult.Imported }));
        }
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

        await foreach (ForkedSignedBeaconBlock _ in fixture.CreateRangeSync(peers).Run(fixture.Chain.AnchorRoot, fixture.Chain.AnchorBlock.Message!.Slot, () => second.Slot, token))
        {
        }

        Assert.That(requests, Is.EqualTo(new[] { (first.Slot, 2UL), (second.Slot, 1UL) }), "round 0 covers the batch, the next round only the slot still lacking the column");
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

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly BeaconChainStore _store = new(new MemColumnsDb<BeaconChainDbColumns>());
        private BeaconDiscovery _discovery = null!;
        private ManualTimestamper _time = null!;

        public ImportableBlobBlock Chain { get; } = ImportableBlobBlock.Create();
        public DataColumnSidecarPool SidecarPool { get; } = new();

        public BeaconDiscovery Discovery => _discovery;

        /// <summary>At epoch 1, which keeps the epoch-0 block inside the data availability window.</summary>
        public SlotClock Clock { get; private set; } = null!;

        public IBlockImporter Importer { get; private set; } = null!;

        /// <summary>This node's sampled columns, which depend on its identity: the fixed key seeded in <see cref="Create"/>.</summary>
        public ulong[] Sampled { get; private set; } = [];

        public static Fixture Create()
        {
            Fixture fixture = new();
            fixture._store.PutMetadata(BeaconDiscovery.IdentityMetadataKey, TestItem.PrivateKeyA.KeyBytes);
            fixture._discovery = new BeaconDiscovery(new BeaconChainConfig { Discv5Port = 0 }, fixture.Chain.Spec, fixture._store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
            // Resolves the identity and local custody exactly as Start does, without binding a socket.
            fixture._discovery.CreateDiscv5Services(IPAddress.Loopback);
            fixture.Sampled = [.. new DiscoveryNodeCustodySource(fixture._discovery).Current!.SampledColumns];
            fixture._time = new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(fixture.Chain.Spec.GenesisTime + fixture.Chain.Spec.SlotsPerEpoch * fixture.Chain.Spec.SecondsPerSlot)).UtcDateTime);
            fixture.Clock = new SlotClock(fixture.Chain.Spec, fixture._time);
            fixture.Importer = new BlockImporterFactory(fixture.Chain.Spec, fixture._store, fixture.Chain.Pubkeys, new NoOpEngineDriver(), new BeaconChainConfig(), LimboLogs.Instance, fixture.SidecarPool, fixture.Clock, fixture._discovery)
                .Create(new ForkedBeaconState.OfFulu(fixture.Chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.AnchorBlock), fixture.Chain.AnchorRoot);
            return fixture;
        }

        public void AdvanceSlots(ulong slots) => _time.Add(TimeSpan.FromSeconds(slots * Chain.Spec.SecondsPerSlot));

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
            new(new StubPool(peers), LimboLogs.Instance, SidecarPool, Chain.Spec, Clock, _discovery);

        public async Task<IReadOnlyList<ForkedSignedBeaconBlock>> RunOneRoundAsync(IBeaconSyncPeer[] peers, CancellationToken token)
        {
            List<ForkedSignedBeaconBlock> yielded = [];
            await foreach (ForkedSignedBeaconBlock block in CreateRangeSync(peers).Run(Chain.AnchorRoot, Chain.AnchorBlock.Message!.Slot, () => Chain.Block.Message!.Slot, token))
            {
                yielded.Add(block);
            }

            return yielded;
        }

        public BeaconSyncOrchestrator CreateOrchestrator(params IBeaconSyncPeer[] peers)
        {
            StubPool pool = new(peers);
            BeaconSyncOrchestrator orchestrator = new(
                new BeaconChainConfig(),
                Chain.Spec,
                _store,
                new BlockImporterFactory(Chain.Spec, _store, Chain.Pubkeys, new NoOpEngineDriver(), new BeaconChainConfig(), LimboLogs.Instance, SidecarPool, Clock, _discovery),
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

        public ValueTask DisposeAsync() => _discovery.DisposeAsync();
    }
}
