// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class InboundBanTests
{
    [Test]
    public void Proven_misconduct_ban_expires_independently_of_outbound_pacing(
        [Values(DisconnectReason.BreachOfProtocol, DisconnectReason.TxFlooding, DisconnectReason.MessageLimitsBreached)] DisconnectReason reason)
    {
        NodeStatsLight stats = new(new Node(TestItem.PublicKeyA, "1.2.3.4", 30303));
        DateTime before = DateTime.UtcNow;
        stats.AddNodeStatsDisconnectEvent(DisconnectType.Local, reason);
        DateTime after = DateTime.UtcNow;
        stats.AddNodeStatsDisconnectEvent(DisconnectType.Remote, DisconnectReason.TooManyPeers);
        stats.AddNodeStatsEvent(NodeStatsEventType.ConnectionFailed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.IsInboundBanned(before.AddMinutes(14)), Is.True);
            Assert.That(stats.IsInboundBanned(after.AddMinutes(15)), Is.False);
        }
    }

    [Test]
    public void Remote_claim_or_ambiguous_local_failure_does_not_ban_inbound(
        [Values(DisconnectReason.BreachOfProtocol, DisconnectReason.TooManyPeers, DisconnectReason.ClientQuitting)] DisconnectReason remoteReason,
        [Values(DisconnectReason.UselessPeer, DisconnectReason.ReceiveMessageTimeout, DisconnectReason.TooManyPeers)] DisconnectReason localReason)
    {
        NodeStatsLight stats = new(new Node(TestItem.PublicKeyA, "1.2.3.4", 30303));
        stats.AddNodeStatsDisconnectEvent(DisconnectType.Remote, remoteReason);
        stats.AddNodeStatsDisconnectEvent(DisconnectType.Local, localReason);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.IsConnectionDelayed(DateTime.UtcNow).Result, Is.True);
            Assert.That(stats.IsInboundBanned(DateTime.UtcNow), Is.False);
        }
    }
}
