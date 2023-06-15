// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.NodeData;
using Nethermind.Network.P2P.Subprotocols.NodeData.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.NodeData;

[Parallelizable(ParallelScope.All)]
public class NodeDataMessageTests
{
    [Test]
    public void Accepts_nulls_inside()
    {
        byte[][] data = { new byte[] { 1, 2, 3 }, null };
        NodeDataMessage message = new(data);
        data.Should().BeEquivalentTo(message.Data);
    }

    [Test]
    public void Accepts_nulls_top_level()
    {
        NodeDataMessage message = new(null);
        message.Data.Length.Should().Be(0);
    }

    [Test]
    public void Sets_values_from_constructor_argument()
    {
        byte[][] data = { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } };
        NodeDataMessage message = new(data);
        data.Should().BeEquivalentTo(message.Data);
    }

    [Test]
    public void To_string()
    {
        NodeDataMessage message = new(new byte[][] { });
        _ = message.ToString();
    }

    [Test]
    public void Packet_type_and_protocol_are_correct()
    {
        byte[][] data = { new byte[] { 1, 2, 3 }, null };
        NodeDataMessage message = new(data);

        message.PacketType.Should().Be(NodeDataMessageCode.NodeData);
        message.Protocol.Should().Be(Protocol.NodeData);
    }
}
