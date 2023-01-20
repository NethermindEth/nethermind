// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Threading;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.NodeData;
using Nethermind.Network.P2P.Subprotocols.NodeData.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.NodeData;

public class NodeDataProtocolHandlerTests
{
    private ISession _session;
    private IMessageSerializationService _svc;
    private ISyncServer _syncServer;
    private Block _genesisBlock;
    private NodeDataProtocolHandler _handler;

    [SetUp]
    public void Setup()
    {
        _svc = Build.A.SerializationService().WithNodeData().TestObject;

        NetworkDiagTracer.IsEnabled = true;

        _session = Substitute.For<ISession>();
        Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
        _session.Node.Returns(node);
        _syncServer = Substitute.For<ISyncServer>();

        _genesisBlock = Build.A.Block.Genesis.TestObject;
        _syncServer.Head.Returns(_genesisBlock.Header);
        _syncServer.Genesis.Returns(_genesisBlock.Header);
        ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
        _handler = new NodeDataProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(timerFactory, LimboLogs.Instance),
            _syncServer,
            LimboLogs.Instance);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
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
        var msg = new NodeDataMessage(System.Array.Empty<byte[]>());

        ((INodeDataPeer)_handler).GetNodeData(new List<Keccak>(new[] { Keccak.Zero }), CancellationToken.None);
        HandleZeroMessage(msg, NodeDataMessageCode.NodeData);
    }

    [Test]
    public void Should_throw_when_receiving_unrequested_node_data()
    {
        var msg = new NodeDataMessage(System.Array.Empty<byte[]>());

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
