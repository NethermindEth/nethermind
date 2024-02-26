// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.NodeData;
using Nethermind.Network.P2P.Subprotocols.NodeData.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.NodeData;

public class NodeDataProtocolHandlerTests
{
    private ISession _session;
    private IMessageSerializationService _svc;
    private NodeDataProtocolHandler _handler;

    [SetUp]
    public void Setup()
    {
        _svc = Build.A.SerializationService().WithNodeData().TestObject;
        _session = Substitute.For<ISession>();
        _handler = new NodeDataProtocolHandler(
            _session,
            _svc,
            Substitute.For<INodeStatsManager>(),
            Substitute.For<ISyncServer>(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
        _session.Dispose();
    }

    [Test]
    public void Metadata_correct()
    {
        _handler.ProtocolCode.Should().Be("nodedata");
        _handler.Name.Should().Be("nodedata1");
        _handler.ProtocolVersion.Should().Be(1);
        _handler.MessageIdSpaceSize.Should().Be(2);
    }

    [Test]
    public void Can_handle_get_node_data()
    {
        var msg = new GetNodeDataMessage(new[] { Keccak.Zero, TestItem.KeccakA });

        HandleZeroMessage(msg, NodeDataMessageCode.GetNodeData);
        _session.Received().DeliverMessage(Arg.Any<NodeDataMessage>());
    }

    [Test]
    public void Can_handle_node_data()
    {
        var msg = new NodeDataMessage(ArrayPoolList<byte[]>.Empty());

        ((INodeDataPeer)_handler).GetNodeData(new List<Hash256>(new[] { Keccak.Zero }), CancellationToken.None);
        HandleZeroMessage(msg, NodeDataMessageCode.NodeData);
    }

    [Test]
    public void Should_throw_when_receiving_unrequested_node_data()
    {
        var msg = new NodeDataMessage(ArrayPoolList<byte[]>.Empty());

        System.Action act = () => HandleZeroMessage(msg, NodeDataMessageCode.NodeData);
        act.Should().Throw<SubprotocolException>();
    }

    private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
    {
        IByteBuffer getPacket = _svc.ZeroSerialize(msg);
        getPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(getPacket) { PacketType = (byte)messageCode });
    }
}
