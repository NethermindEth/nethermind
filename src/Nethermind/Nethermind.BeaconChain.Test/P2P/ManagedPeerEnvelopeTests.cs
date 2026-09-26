// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private const ulong AnchorSlot = 13_410_304;

    [Test]
    [CancelAfter(60_000)]
    public async Task Envelope_requests_reach_the_protocol_they_name(CancellationToken token)
    {
        ExecutionPayloadEnvelopePool serverPool = new();
        SignedExecutionPayloadEnvelope first = Envelope(AnchorSlot);
        SignedExecutionPayloadEnvelope second = Envelope(AnchorSlot + 7);
        serverPool.Add(first.Message!.BeaconBlockRoot!, AnchorSlot, first);
        serverPool.Add(second.Message!.BeaconBlockRoot!, AnchorSlot + 7, second);

        (BeaconP2P server, _, _) = CreateNode(serverPool);
        (BeaconP2P client, BeaconChainStatusHolder clientStatus, BeaconChainConfig clientConfig) = CreateNode(new ExecutionPayloadEnvelopePool());

        await using (client)
        await using (server)
        {
            await server.StartAsync(token);
            await client.StartAsync(token);

            PeerManager peerManager = new(client, clientConfig, clientStatus, LimboLogs.Instance);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(server), token), Is.True);
            IBeaconSyncPeer peer = peerManager.GetBestPeers(0).Single();

            IReadOnlyList<SignedExecutionPayloadEnvelope> byRange = await peer.RequestExecutionPayloadEnvelopesByRangeAsync(AnchorSlot, 8, token);
            IReadOnlyList<SignedExecutionPayloadEnvelope> byRoot = await peer.RequestExecutionPayloadEnvelopesByRootAsync([second.Message.BeaconBlockRoot!], token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(byRange.Select(e => e.Message!.BeaconBlockRoot), Is.EqualTo(new[] { first.Message.BeaconBlockRoot, second.Message.BeaconBlockRoot }), "by range serves both edge slots of the window");
                Assert.That(byRoot.Select(e => e.Message!.BeaconBlockRoot), Is.EqualTo(new[] { second.Message.BeaconBlockRoot }), "by root serves only the requested root");
            }
        }
    }

    private static SignedExecutionPayloadEnvelope Envelope(ulong slot) => new()
    {
        Message = new ExecutionPayloadEnvelope
        {
            Payload = new ExecutionPayloadGloas { SlotNumber = slot },
            ExecutionRequests = new ExecutionRequestsGloas(),
            BuilderIndex = 3,
            BeaconBlockRoot = new Hash256([.. BitConverter.GetBytes(slot), .. new byte[24]]),
            ParentBeaconBlockRoot = Hash256.Zero,
        },
        Signature = new BlsSignature(new byte[BlsSignature.Length]),
    };

    private static string LoopbackAddress(BeaconP2P node)
    {
        string address = node.ListenAddresses.First().ToString().Replace("0.0.0.0", "127.0.0.1");
        if (!address.Contains("/p2p/"))
        {
            address += $"/p2p/{node.LocalPeerId}";
        }

        return address;
    }

    private static (BeaconP2P P2P, BeaconChainStatusHolder StatusHolder, BeaconChainConfig Config) CreateNode(ExecutionPayloadEnvelopePool envelopePool)
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
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        BeaconP2P p2p = new(config, Spec, store, statusHolder, new LocalMetadataSource(), new DataColumnSidecarPool(), envelopePool, LimboLogs.Instance);
        return (p2p, statusHolder, config);
    }
}
