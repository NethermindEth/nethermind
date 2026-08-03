// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Snap.V1;
using Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages;
using Nethermind.Network.P2P.Subprotocols.Snap.V2;
using Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.V2;

public class Snap2ProtocolHandlerTests
{
    private static Snap2ProtocolHandler CreateHandler(ISession session, ISnapServer snapServer, IMessageSerializationService serializer) =>
        new(
            session,
            Substitute.For<INodeStatsManager>(),
            serializer,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            new SyncConfig(),
            snapServer);

    private static void Deliver<T>(Snap2ProtocolHandler handler, IMessageSerializationService serializer, T message, int packetType) where T : P2PMessage
    {
        IByteBuffer? buffer = serializer.ZeroSerialize(message);
        try
        {
            buffer.ReadByte(); // skip the adaptive protocol-type byte
            ZeroPacket packet = new(buffer) { PacketType = (byte)packetType };
            buffer = null;
            try
            {
                handler.HandleMessage(packet);
            }
            finally
            {
                ReferenceCountUtil.Release(packet);
            }
        }
        finally
        {
            buffer?.SafeRelease();
        }
    }

    [Test]
    public void GetBlockAccessLists_forwards_requested_byte_budget_to_snap_server()
    {
        ISnapServer snapServer = Substitute.For<ISnapServer>();
        snapServer.CanServe.Returns(true);
        snapServer.GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EmptyByteArrayList.Instance);
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetBlockAccessListsMessageSerializer()),
            SerializerInfo.Create(new BlockAccessListsMessageSerializer()));

        Snap2ProtocolHandler handler = CreateHandler(session, snapServer, serializer);

        const long requestedBytes = 1234;
        using GetBlockAccessListsMessage request = new()
        {
            RequestId = 1,
            BlockHashes = new ArrayPoolList<ValueHash256>(1) { TestItem.KeccakA.ValueHash256 },
            Bytes = requestedBytes,
        };

        Deliver(handler, serializer, request, Snap2MessageCode.GetBlockAccessLists);

        snapServer.Received(1).GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), requestedBytes, Arg.Any<CancellationToken>());
    }

    [Test]
    public void GetTrieNodes_is_a_protocol_breach_and_is_not_served()
    {
        // snap/2 removed GetTrieNodes/TrieNodes (EIP-8189); the peer must be disconnected, not served.
        ISnapServer snapServer = Substitute.For<ISnapServer>();
        snapServer.CanServe.Returns(true);
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetTrieNodesMessageSerializer()),
            SerializerInfo.Create(new TrieNodesMessageSerializer()));

        Snap2ProtocolHandler handler = CreateHandler(session, snapServer, serializer);

        using GetTrieNodesMessage request = new()
        {
            RequestId = 1,
            RootHash = Keccak.Zero,
            Paths = PathGroup.EncodeToRlpPathGroupList([]),
            Bytes = 1234,
        };

        Deliver(handler, serializer, request, Snap1MessageCode.GetTrieNodes);

        snapServer.DidNotReceive().GetTrieNodes(Arg.Any<IReadOnlyList<PathGroup>>(), Arg.Any<Hash256>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        session.Received().InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
    }
}
