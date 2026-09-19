// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
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

            IReadOnlyList<SignedBeaconBlock> blocks = await client.P2P.RequestBlocksByRangeAsync(toServer, AnchorSlot + 1, 8, token);
            Assert.That(blocks.Select(b => SszRoots.HashTreeRoot(b.Message!)), Is.EqualTo(chainRoots), "blocks by range roots");

            IReadOnlyList<SignedBeaconBlock> byRoot = await client.P2P.RequestBlocksByRootAsync(toServer, [chainRoots[1]], token);
            Assert.That(byRoot.Select(b => SszRoots.HashTreeRoot(b.Message!)), Is.EqualTo(new[] { chainRoots[1] }), "blocks by root");

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

    private record Node(BeaconP2P P2P, BeaconChainStore Store, BeaconChainStatusHolder StatusHolder, LocalMetadataSource MetadataSource, BeaconChainConfig Config);

    private static Node CreateNode()
    {
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        BeaconChainStatusHolder statusHolder = new(Spec, Timestamper.Default);
        LocalMetadataSource metadataSource = new();
        BeaconP2P p2p = new(config, Spec, store, statusHolder, metadataSource, LimboLogs.Instance);
        return new Node(p2p, store, statusHolder, metadataSource, config);
    }
}
