// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Network.Libp2p;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

/// <summary>Gossipsub v1.2 IDONTWANT under the host's settings, with simulated v1.2 peers.</summary>
public partial class GossipRouterTests
{
    /// <summary>
    /// A large message is announced with IDONTWANT to the other v1.2 mesh peers only once its deferred verdict accepts it, ahead of the
    /// message itself, and never to the peer that delivered it nor for a message that is not accepted.
    /// </summary>
    /// <remarks>gossipsub v1.2: IDONTWANT tells mesh peers not to send a message already received; it follows validation, so an unvalidated message is not announced.</remarks>
    [TestCase(MessageValidity.Accepted)]
    [TestCase(MessageValidity.Rejected)]
    [TestCase(MessageValidity.Ignored)]
    public async Task Large_message_is_announced_with_idontwant_only_once_accepted_and_before_it_is_sent(MessageValidity validity)
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 8, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), PubsubRouter.GossipsubProtocolVersionV12);
        byte[] block = LargeBlockMessage(CurrentSlot);

        fixture.Receive(fixture.Sender, block);
        int announcedWhilePending = fixture.IdontwantsTo(fixture.Neighbor).Count();
        fixture.Raised.Single().Complete(validity);

        Rpc[] toNeighbor = [.. fixture.SentTo(fixture.Neighbor)];
        int announcement = Array.FindIndex(toNeighbor, static rpc => rpc.Control?.Idontwant.Count > 0);
        int forwarded = Array.FindIndex(toNeighbor, rpc => rpc.Publish.Any(message => message.Data.Span.SequenceEqual(block)));
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(announcedWhilePending, Is.Zero, "a message awaiting its verdict is not announced");
        Assert.That(fixture.IdontwantsTo(fixture.Sender), Is.Empty, "the delivering peer is not told");
        if (validity == MessageValidity.Accepted)
        {
            Assert.That(fixture.IdontwantsTo(fixture.Neighbor), Is.EqualTo(new[] { new MessageId(Eth2MessageId.Compute(BlockTopic, block)) }));
            Assert.That(announcement, Is.GreaterThanOrEqualTo(0).And.LessThan(forwarded), "the announcement goes ahead of the message");
        }
        else
        {
            Assert.That((announcement, forwarded), Is.EqualTo((-1, -1)));
        }
    }

    /// <summary>
    /// A v1.2 peer's IDONTWANT stops this node sending it that message until the announcement expires. A heartbeat's budget keeps two blocks
    /// of announcements sent one entry each, and announcements past it are not kept, so no peer can make the node withhold every message.
    /// </summary>
    [TestCase(0, true, false, TestName = "Peer_idontwant_withholds_the_message_while_unexpired")]
    [TestCase(2, true, false, TestName = "Peer_idontwant_withholds_the_message_for_three_heartbeats")]
    [TestCase(3, true, true, TestName = "Peer_idontwant_expires_after_its_heartbeats")]
    [TestCase(0, false, true, TestName = "Peer_idontwant_past_its_heartbeat_budget_is_not_kept")]
    public async Task Peer_idontwant_withholds_a_message_until_it_expires_within_its_budget(int heartbeats, bool withinBudget, bool expectSent)
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 8, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), PubsubRouter.GossipsubProtocolVersionV12);
        byte[] block = LargeBlockMessage(CurrentSlot);
        const int budget = BeaconP2P.MaxIdontwantControlsPerHeartbeat;
        Rpc announcements = new() { Control = new ControlMessage() };
        // One entry per announced message, as an honest peer sends them: the block's is the last within the budget, or the first past it.
        for (int i = 0; i < (withinBudget ? budget - 1 : budget); i++)
        {
            byte[] filler = new byte[20];
            BitConverter.TryWriteBytes(filler, i);
            announcements.Control.Idontwant.Add(new ControlIDontWant { MessageIDs = { ByteString.CopyFrom(filler) } });
        }

        announcements.Control.Idontwant.Add(new ControlIDontWant { MessageIDs = { ByteString.CopyFrom(Eth2MessageId.Compute(BlockTopic, block)) } });
        fixture.ReceiveRpc(fixture.Neighbor, announcements);
        for (int i = 0; i < heartbeats; i++)
        {
            await fixture.Pubsub.Heartbeat();
        }

        fixture.Receive(fixture.Sender, block);
        fixture.Raised.Single().Complete(MessageValidity.Accepted);

        Assert.That(fixture.SentToNeighbor(block), Is.EqualTo(expectSent));
    }

    private static byte[] LargeBlockMessage(ulong slot)
    {
        SignedBeaconBlock block = TestChain.CreateBlock(slot, Hash256.Zero);
        byte[] transaction = new byte[4096];
        new Random(1).NextBytes(transaction);
        block.Message!.Body!.ExecutionPayload!.Transactions = [new Transaction { Bytes = transaction }];
        return Snappy.CompressToArray(SignedBeaconBlock.Encode(block));
    }
}
