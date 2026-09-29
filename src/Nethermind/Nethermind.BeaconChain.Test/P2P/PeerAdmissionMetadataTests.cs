// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerBandTests;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// The admission <c>MetaData</c> request runs while the dial holds its outbound slot, so a peer that never answers it must
/// not keep the slot for the full request timeout, and the peer is usable meanwhile on the custody its ENR or the floor gives.
/// </summary>
public class PeerAdmissionMetadataTests
{
    // The metadata timeout plus margin for a loaded loopback dial, still under the 15 s request timeout a missing bound would cost.
    private static readonly TimeSpan Within = PeerManager.AdmissionMetadataTimeout + TimeSpan.FromSeconds(5);

    /// <summary>A waiter for a custodian learns of the peer when it can be asked, so it is in the pool and its custody is known when the event is raised.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task An_admitted_peer_is_announced_once_it_is_in_the_pool(CancellationToken token)
    {
        Node client = CreateNode();
        Node other = CreateNode();
        SetMatchingStatus(client, other);

        await using (client.P2P)
        await using (other.P2P)
        {
            await client.P2P.StartAsync(token);
            await other.P2P.StartAsync(token);
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
            List<(IBeaconSyncPeer Peer, bool InPool, bool CustodyKnown)> announced = [];
            peerManager.PeerAdmitted += peer => announced.Add((peer, peerManager.GetBestPeers(0).Contains(peer), peer.Custody.IsAdvertised));

            bool admitted = await peerManager.TryAddPeerAsync(LoopbackAddress(other.P2P), token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(admitted, Is.True);
                Assert.That(announced, Has.Count.EqualTo(1));
                Assert.That(announced[0].InPool, Is.True);
                Assert.That(announced[0].CustodyKnown, Is.True, "the peer's MetaData was read before the announcement");
            }
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_that_never_answers_metadata_is_admitted_within_the_admission_timeout_and_frees_the_dial_slot(CancellationToken token)
    {
        Node client = CreateNode();
        Node other = CreateNode();
        SetMatchingStatus(client, other);
        client.Config.MaxConcurrentOutboundDials = 1;
        // Answers identify and status like a beacon node but does not list the metadata protocol, which the pinned multistream leaves unanswered.
        await using PlainPeer silent = await PlainPeer.StartAsync(static settings => new Nethermind.Libp2p.Protocols.IdentifyProtocol(settings), token,
            new ScriptedStatusSource(_ => client.StatusHolder.CurrentStatus));

        await using (client.P2P)
        await using (other.P2P)
        {
            await client.P2P.StartAsync(token);
            await other.P2P.StartAsync(token);
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
            string silentAddress = silent.Address.ToString();
            if (!silentAddress.Contains("/p2p/", StringComparison.Ordinal))
            {
                silentAddress += $"/p2p/{silent.Peer.Identity.PeerId}";
            }

            Task<bool> silentAdmission = peerManager.TryAddPeerAsync(silentAddress, token);
            await PeerSessionNodes.WaitUntilAsync(() => peerManager.PeerCount == 1, "fixture: the silent peer was never recorded", token);
            bool usableWhileMetadataPends = !silentAdmission.IsCompleted && peerManager.GetBestPeers(0).Count == 1;
            Task<bool> otherAdmission = peerManager.TryAddPeerAsync(LoopbackAddress(other.P2P), token);

            Task both = Task.WhenAll(silentAdmission, otherAdmission);
            bool inTime = await Task.WhenAny(both, Task.Delay(Within, token)) == both;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(usableWhileMetadataPends, Is.True, "the peer is in the pool while its metadata is awaited");
                Assert.That(inTime, Is.True, "the admission ends within the metadata timeout, so the next dial gets the slot");
                Assert.That(inTime && silentAdmission.Result, Is.True);
                Assert.That(inTime && otherAdmission.Result, Is.True);
                Assert.That(peerManager.PeerCount, Is.EqualTo(2));
            }
        }
    }
}
