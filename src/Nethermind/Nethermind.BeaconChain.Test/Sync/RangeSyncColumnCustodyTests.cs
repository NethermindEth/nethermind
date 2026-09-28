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
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Api;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
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

        List<(IBeaconSyncPeer Peer, ulong[] Columns)> requests = RangeSync.AssignColumns([0, 1, 2, 3, 4, 5], [floor, advertised, .. singles], maxPeers: 4);

        Assert.That(requests.Select(static r => (r.Peer.Id, r.Columns)), Is.EqualTo(new (string, ulong[])[]
        {
            ("floor", [0]),
            ("advertised", [1]),
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

        BlockImportResult first = await orchestrator.ImportBlockAsync(block, token);
        serving = true;
        BlockImportResult sameSlot = await orchestrator.ImportBlockAsync(block, token);
        int requestsBeforeTick = custodian.RootColumnRequests;
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot + 1, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((first, sameSlot), Is.EqualTo((BlockImportResult.DataUnavailable, BlockImportResult.DataUnavailable)));
            Assert.That(requestsBeforeTick, Is.EqualTo(1), "the second deferral in the same slot does not fetch again");
            Assert.That(custodian.RootColumnRequests, Is.EqualTo(2), "the slot tick's retry fetches again");
            Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the retry imports the block once its columns arrived");
            Assert.That(bystander.RootColumnRequests, Is.Zero, "a peer custodying no sampled column is never asked");
        }
    }

    private static StubPeer PeerWithCustody(string id, PeerColumnCustody custody) => new(id, headSlot: 0, static (_, _) => [], custody: custody);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly BeaconChainStore _store = new(new MemColumnsDb<BeaconChainDbColumns>());
        private BeaconDiscovery _discovery = null!;

        public ImportableBlobBlock Chain { get; } = ImportableBlobBlock.Create();
        public DataColumnSidecarPool SidecarPool { get; } = new();

        /// <summary>At epoch 1, which keeps the epoch-0 block inside the data availability window.</summary>
        public SlotClock Clock { get; private set; } = null!;

        public IBlockImporter Importer { get; private set; } = null!;

        /// <summary>This node's sampled columns, which depend on its randomly generated identity.</summary>
        public ulong[] Sampled { get; private set; } = [];

        public static Fixture Create()
        {
            Fixture fixture = new();
            fixture._discovery = new BeaconDiscovery(new BeaconChainConfig { Discv5Port = 0 }, fixture.Chain.Spec, fixture._store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
            // Resolves the identity and local custody exactly as Start does, without binding a socket.
            fixture._discovery.CreateDiscv5Services(IPAddress.Loopback);
            fixture.Sampled = [.. new DiscoveryNodeCustodySource(fixture._discovery).Current!.SampledColumns];
            fixture.Clock = fixture.Chain.ClockAtEpoch(1);
            fixture.Importer = new BlockImporterFactory(fixture.Chain.Spec, fixture._store, fixture.Chain.Pubkeys, new NoOpEngineDriver(), new BeaconChainConfig(), LimboLogs.Instance, fixture.SidecarPool, fixture.Clock, fixture._discovery)
                .Create(fixture.Chain.AnchorState, fixture.Chain.AnchorBlock, fixture.Chain.AnchorRoot);
            return fixture;
        }

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
            orchestrator.Initialize(Importer, Chain.AnchorBlock, Chain.AnchorRoot);
            return orchestrator;
        }

        public ValueTask DisposeAsync() => _discovery.DisposeAsync();
    }
}
