// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>The execution payload envelope requests a sync peer handed out by <see cref="PeerManager"/> makes over a live session.</summary>
public class ManagedPeerEnvelopeTests
{
    private static readonly BeaconChainSpec Spec = EnvelopeChain.Spec;
    private static readonly ulong AnchorSlot = Spec.GloasForkEpoch * Spec.SlotsPerEpoch + 1;

    [Test]
    [CancelAfter(60_000)]
    public async Task Envelope_requests_reach_the_protocol_they_name(CancellationToken token)
    {
        EnvelopeChain chain = new();
        (Hash256 firstRoot, Hash256 firstHash) = chain.Put(AnchorSlot, Hash256.Zero, Hash256.Zero);
        (Hash256 secondRoot, Hash256 secondHash) = chain.Put(AnchorSlot + 7, firstRoot, firstHash);
        (Hash256 headRoot, _) = chain.Put(AnchorSlot + 8, secondRoot, secondHash);
        chain.AddEnvelopes(firstRoot, secondRoot);
        chain.SetHead(headRoot, AnchorSlot + 8);

        (BeaconP2P server, _, _) = CreateNode(chain.Pool, chain.Store);
        (BeaconP2P client, BeaconChainStatusHolder clientStatus, BeaconChainConfig clientConfig) = CreateNode(new ExecutionPayloadEnvelopePool(), new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()));

        await using (client)
        await using (server)
        {
            await server.StartAsync(token);
            await client.StartAsync(token);

            PeerManager peerManager = new(client, clientConfig, clientStatus, LimboLogs.Instance);
            Assert.That(await peerManager.TryAddPeerAsync(PeerSessionNodes.LoopbackAddressText(server), token), Is.True);
            IBeaconSyncPeer peer = peerManager.GetBestPeers(0).Single();

            IReadOnlyList<SignedExecutionPayloadEnvelope> byRange = await peer.RequestExecutionPayloadEnvelopesByRangeAsync(AnchorSlot, 8, token);
            IReadOnlyList<SignedExecutionPayloadEnvelope> byRoot = await peer.RequestExecutionPayloadEnvelopesByRootAsync([secondRoot], token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(byRange.Select(e => e.Message!.BeaconBlockRoot), Is.EqualTo(new[] { firstRoot, secondRoot }), "by range serves both edge slots of the window");
                Assert.That(byRoot.Select(e => e.Message!.BeaconBlockRoot), Is.EqualTo(new[] { secondRoot }), "by root serves only the requested root");
            }
        }
    }

    private static (BeaconP2P P2P, BeaconChainStatusHolder StatusHolder, BeaconChainConfig Config) CreateNode(ExecutionPayloadEnvelopePool envelopePool, BeaconChainStore store)
    {
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStatusHolder statusHolder = new(Spec, Timestamper.Default)
        {
            CurrentStatus = new StatusMessageV2
            {
                ForkDigest = ForkDigest.Compute(Spec, Spec.GetEpoch(AnchorSlot)),
                FinalizedRoot = Hash256.Zero,
                FinalizedEpoch = Spec.GetEpoch(AnchorSlot),
                HeadRoot = Hash256.Zero,
                HeadSlot = AnchorSlot,
                EarliestAvailableSlot = AnchorSlot,
            },
        };
        BeaconP2P p2p = new(config, Spec, store, statusHolder, new LocalMetadataSource(), new DataColumnSidecarPool(), envelopePool, LimboLogs.Instance);
        return (p2p, statusHolder, config);
    }
}
