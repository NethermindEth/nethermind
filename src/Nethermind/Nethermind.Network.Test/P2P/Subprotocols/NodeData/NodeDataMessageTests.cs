// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Collections;
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
        ArrayPoolList<byte[]> data = new(2) { new byte[] { 1, 2, 3 }, null };
        using NodeDataMessage message = new(new ByteArrayListAdapter(data));
        message.Data.Count.Should().Be(2);
        message.Data[0].ToArray().Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        message.Data[1].ToArray().Should().BeEquivalentTo(Array.Empty<byte>());
    }

    [Test]
    public void Accepts_nulls_top_level()
    {
        using NodeDataMessage message = new(null);
        message.Data.Count.Should().Be(0);
    }

    [Test]
    public void Sets_values_from_constructor_argument()
    {
        ArrayPoolList<byte[]> data = new(2) { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } };
        using NodeDataMessage message = new(new ByteArrayListAdapter(data));
        message.Data.Count.Should().Be(2);
        message.Data[0].ToArray().Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        message.Data[1].ToArray().Should().BeEquivalentTo(new byte[] { 4, 5, 6 });
    }

    [Test]
    public void To_string()
    {
        using NodeDataMessage message = new(new ByteArrayListAdapter(ArrayPoolList<byte[]>.Empty()));
        _ = message.ToString();
    }

    [Test]
    public void Packet_type_and_protocol_are_correct()
    {
        ArrayPoolList<byte[]> data = new(2) { new byte[] { 1, 2, 3 }, null };
        using NodeDataMessage message = new(new ByteArrayListAdapter(data));

        message.PacketType.Should().Be(NodeDataMessageCode.NodeData);
        message.Protocol.Should().Be(Protocol.NodeData);
    }
}
