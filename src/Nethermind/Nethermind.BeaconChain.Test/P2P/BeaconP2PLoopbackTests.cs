// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

public class BeaconP2PLoopbackTests
{
    // A Fulu/BPO2-era mainnet slot so the fork digest exercises the EIP-7892 blob-parameter masking.
    private const ulong AnchorSlot = 13_410_304;

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [Test]
    [CancelAfter(120_000)]
    public async Task Two_hosts_exchange_status_blocks_ping_metadata_and_goodbye(CancellationToken token)
    {
        // Slot AnchorSlot + 3 stays empty to exercise skipped slots in the range response.
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] chain) =
            TestChain.BuildLinkedChain(AnchorSlot, AnchorSlot + 1, AnchorSlot + 2, AnchorSlot + 4);
        Hash256[] chainRoots = [.. chain.Select(b => SszRoots.HashTreeRoot(b.Message!))];

        byte[] forkDigest = ForkDigest.Compute(Spec, Spec.GetEpoch(AnchorSlot));
        StatusMessageV2 serverStatus = new()
        {
            ForkDigest = forkDigest,
            FinalizedRoot = anchorRoot,
            FinalizedEpoch = Spec.GetEpoch(AnchorSlot),
            HeadRoot = chainRoots[^1],
            HeadSlot = AnchorSlot + 4,
            EarliestAvailableSlot = AnchorSlot,
        };

        Node server = CreateNode();
        TestChain.Persist(server.Store, anchor, anchorRoot, chain);
        server.StatusHolder.CurrentStatus = serverStatus;
        server.MetadataSource.Current.SeqNumber = 42;
        server.MetadataSource.Current.CustodyGroupCount = 128;

        Node client = CreateNode();
        client.StatusHolder.CurrentStatus = new StatusMessageV2
        {
            ForkDigest = forkDigest,
            FinalizedRoot = anchorRoot,
            FinalizedEpoch = Spec.GetEpoch(AnchorSlot),
            HeadRoot = anchorRoot,
            HeadSlot = AnchorSlot,
            EarliestAvailableSlot = AnchorSlot,
        };

        await using (client.P2P)
        await using (server.P2P)
        {
            await server.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);

            ISession toServer = await client.P2P.DialPeerAsync(LoopbackAddress(server.P2P), token);
            ISession toClient = await server.P2P.DialPeerAsync(LoopbackAddress(client.P2P), token);

            StatusMessageV2 statusSeenByClient = await client.P2P.RequestStatusAsync(toServer, token);
            StatusMessageV2 statusSeenByServer = await server.P2P.RequestStatusAsync(toClient, token);
            AssertStatus(statusSeenByClient, serverStatus);
            AssertStatus(statusSeenByServer, client.StatusHolder.CurrentStatus);

            IReadOnlyList<ForkedSignedBeaconBlock> blocks = await client.P2P.RequestBlocksByRangeAsync(toServer, AnchorSlot + 1, 8, token);
            Assert.That(blocks.Select(b => b.ComputeMessageRoot()), Is.EqualTo(chainRoots), "blocks by range roots");

            IReadOnlyList<ForkedSignedBeaconBlock> byRoot = await client.P2P.RequestBlocksByRootAsync(toServer, [chainRoots[1]], token);
            Assert.That(byRoot.Select(b => b.ComputeMessageRoot()), Is.EqualTo(new[] { chainRoots[1] }), "blocks by root");

            Assert.That(await client.P2P.PingAsync(toServer, token), Is.EqualTo(42ul), "ping returns the server metadata seq");

            MetaDataV3 metadata = await client.P2P.RequestMetaDataAsync(toServer, token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(metadata.SeqNumber, Is.EqualTo(42ul), "metadata seq");
                Assert.That(metadata.CustodyGroupCount, Is.EqualTo(128ul), "metadata custody group count");
                Assert.That(metadata.Attnets, Is.EqualTo(new BitArray(64)), "metadata attnets");
            }

            // Peer manager: one maintenance round over a static peer entry connects and records its status.
            client.Config.StaticPeers = LoopbackAddress(server.P2P).ToString();
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
            await peerManager.RunMaintenanceRoundAsync(token);
            IBeaconSyncPeer syncPeer = peerManager.GetBestPeers(AnchorSlot + 4).Single();
            Assert.That(syncPeer.HeadSlot, Is.EqualTo(AnchorSlot + 4), "peer manager records the peer head");

            await client.P2P.GoodbyeAsync(toServer, GoodbyeReason.ClientShutdown, token);
        }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Column_dials_resolve_to_the_requested_shape_on_the_shared_protocols(CancellationToken token)
    {
        BeaconChainSpec spec = BeaconChainSpec.Sepolia;
        ulong gloasSlot = spec.GloasForkEpoch * spec.SlotsPerEpoch + 5;
        Hash256 heldRoot = Keccak.Compute("held Gloas block");
        Hash256 pendingRoot = Keccak.Compute("pending Gloas block");
        StatusMessageV2 status = new()
        {
            ForkDigest = ForkDigest.Compute(spec, spec.GetEpoch(gloasSlot)),
            FinalizedRoot = Hash256.Zero,
            HeadRoot = heldRoot,
            HeadSlot = gloasSlot,
        };

        Node server = CreateNode(spec);
        server.StatusHolder.CurrentStatus = status;
        server.Pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(3, gloasSlot, heldRoot));
        server.Pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(7, gloasSlot, heldRoot));
        server.Pool.AddPendingGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(3, gloasSlot, pendingRoot), "gossip peer");
        DataColumnSidecar fulu = DataColumnSidecarTestFixture.BuildValidSidecar(3, spec.GloasForkEpoch * spec.SlotsPerEpoch - 1, blobCount: 1);
        Hash256 fuluRoot = SszRoots.HashTreeRoot(fulu.SignedBlockHeader!.Message!);
        server.Pool.Add(fuluRoot, fulu.SignedBlockHeader.Message!.Slot, fulu);

        Node client = CreateNode(spec);
        client.StatusHolder.CurrentStatus = status;

        await using (client.P2P)
        await using (server.P2P)
        {
            await server.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);

            client.Config.StaticPeers = LoopbackAddress(server.P2P).ToString();
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
            await peerManager.RunMaintenanceRoundAsync(token);
            IBeaconSyncPeer peer = peerManager.GetBestPeers(gloasSlot).Single();
            long messagesSent = PeerManager.MessagesSentForTest(peer);

            IReadOnlyList<DataColumnSidecarGloas> byRoot = await peer.RequestGloasDataColumnSidecarsByRootAsync(
                [new DataColumnsByRootIdentifier { BlockRoot = heldRoot, Columns = [7, 5, 3] }, new DataColumnsByRootIdentifier { BlockRoot = pendingRoot, Columns = [3] }], token);
            Assert.That(byRoot.Select(static s => (s.BeaconBlockRoot, s.Index, s.Slot)), Is.EqualTo(new[] { (heldRoot, 7UL, gloasSlot), (heldRoot, 3UL, gloasSlot) }),
                "served verified Gloas columns in request order under the Gloas digest, never the pending candidate");
            Assert.That(PeerManager.MessagesSentForTest(peer), Is.EqualTo(messagesSent + 1), "the by-root ask counts as a message sent");

            // The listen side serves only Fulu slots by range; this proves the Gloas closing resolves on the shared protocol id.
            IReadOnlyList<DataColumnSidecarGloas> byRange = await peer.RequestGloasDataColumnSidecarsByRangeAsync(gloasSlot, 1, [3], token);
            Assert.That(byRange, Is.Empty);
            Assert.That(PeerManager.MessagesSentForTest(peer), Is.EqualTo(messagesSent + 2), "the by-range ask counts as a message sent");

            ISession toServer = await client.P2P.DialPeerAsync(LoopbackAddress(server.P2P), token);
            IReadOnlyList<DataColumnSidecar> fuluByRoot = await client.P2P.RequestDataColumnSidecarsByRootAsync(toServer, [new DataColumnsByRootIdentifier { BlockRoot = fuluRoot, Columns = [3] }], token);
            Assert.That(fuluByRoot.Select(static s => s.Index), Is.EqualTo(new[] { 3UL }), "the Fulu dial still reads Fulu chunks");

            using CancellationTokenSource quick = CancellationTokenSource.CreateLinkedTokenSource(token);
            quick.CancelAfter(TimeSpan.FromSeconds(5));
            Assert.That(await peer.RequestGloasDataColumnSidecarsByRootAsync([], quick.Token), Is.Empty, "an empty ask returns at once, not after the response timeout");
            Assert.That(await client.P2P.RequestDataColumnSidecarsByRootAsync(toServer, [], quick.Token), Is.Empty, "an empty ask returns at once, not after the response timeout");
        }
    }

    public enum ColumnDial
    {
        FuluByRange,
        GloasByRange,
        FuluByRoot,
        GloasByRoot,
    }

    [Test]
    public async Task Each_column_request_dials_its_protocol_asking_for_its_own_sidecar_shape([Values] ColumnDial dial)
    {
        ISession session = Substitute.For<ISession>();
        ForkedDataColumnSidecars empty = new([], []);
        session.DialAsync<DataColumnSidecarsByRangeProtocol, DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, ForkedDataColumnSidecars>(default, default).ReturnsForAnyArgs(empty);
        session.DialAsync<DataColumnSidecarsByRootProtocol, DataColumnSidecarsDial<DataColumnsByRootIdentifier[]>, ForkedDataColumnSidecars>(default, default).ReturnsForAnyArgs(empty);
        DataColumnsByRootIdentifier[] identifiers = [new() { BlockRoot = Hash256.Zero, Columns = [3] }];
        Node node = CreateNode();

        await using (node.P2P)
        {
            Task request = dial switch
            {
                ColumnDial.FuluByRange => node.P2P.RequestDataColumnSidecarsByRangeAsync(session, 1, 1, [3], default),
                ColumnDial.GloasByRange => node.P2P.RequestGloasDataColumnSidecarsByRangeAsync(session, 1, 1, [3], default),
                ColumnDial.FuluByRoot => node.P2P.RequestDataColumnSidecarsByRootAsync(session, identifiers, default),
                ColumnDial.GloasByRoot => node.P2P.RequestGloasDataColumnSidecarsByRootAsync(session, identifiers, default),
                _ => throw new ArgumentOutOfRangeException(nameof(dial)),
            };
            await request;
        }

        ICall call = session.ReceivedCalls().Single(static c => c.GetMethodInfo().Name == nameof(ISession.DialAsync));
        bool gloas = call.GetArguments()[0] switch
        {
            DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest> byRange => byRange.Gloas,
            DataColumnSidecarsDial<DataColumnsByRootIdentifier[]> byRoot => byRoot.Gloas,
            var other => throw new AssertionException($"Unexpected dial argument {other}"),
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.GetMethodInfo().GetGenericArguments()[0],
                Is.EqualTo(dial is ColumnDial.FuluByRange or ColumnDial.GloasByRange ? typeof(DataColumnSidecarsByRangeProtocol) : typeof(DataColumnSidecarsByRootProtocol)));
            Assert.That(gloas, Is.EqualTo(dial is ColumnDial.GloasByRange or ColumnDial.GloasByRoot));
        }
    }

    private static void AssertStatus(StatusMessageV2 actual, StatusMessageV2 expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.ForkDigest, Is.EqualTo(expected.ForkDigest), "fork digest");
            Assert.That(actual.FinalizedRoot, Is.EqualTo(expected.FinalizedRoot), "finalized root");
            Assert.That(actual.FinalizedEpoch, Is.EqualTo(expected.FinalizedEpoch), "finalized epoch");
            Assert.That(actual.HeadRoot, Is.EqualTo(expected.HeadRoot), "head root");
            Assert.That(actual.HeadSlot, Is.EqualTo(expected.HeadSlot), "head slot");
            Assert.That(actual.EarliestAvailableSlot, Is.EqualTo(expected.EarliestAvailableSlot), "earliest available slot");
        }
    }

    private static Multiaddress LoopbackAddress(BeaconP2P node)
    {
        string address = node.ListenAddresses.First().ToString().Replace("0.0.0.0", "127.0.0.1");
        if (!address.Contains("/p2p/"))
        {
            address += $"/p2p/{node.LocalPeerId}";
        }

        return Multiaddress.Decode(address);
    }

    private record Node(BeaconP2P P2P, BeaconChainStore Store, BeaconChainStatusHolder StatusHolder, LocalMetadataSource MetadataSource, BeaconChainConfig Config, DataColumnSidecarPool Pool);

    private static Node CreateNode(BeaconChainSpec? spec = null)
    {
        spec ??= Spec;
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        BeaconChainStatusHolder statusHolder = new(spec, Timestamper.Default);
        LocalMetadataSource metadataSource = new();
        DataColumnSidecarPool pool = new();
        BeaconP2P p2p = new(config, spec, store, statusHolder, metadataSource, pool, new ExecutionPayloadEnvelopePool(), LimboLogs.Instance);
        return new Node(p2p, store, statusHolder, metadataSource, config, pool);
    }
}
