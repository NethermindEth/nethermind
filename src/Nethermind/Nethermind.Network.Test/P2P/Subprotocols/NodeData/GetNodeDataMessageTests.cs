// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.NodeData;
using Nethermind.Network.P2P.Subprotocols.NodeData.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.NodeData;

[Parallelizable(ParallelScope.All)]
public class GetNodeDataMessageTests
{
    [Test]
    public void Sets_values_from_constructor_argument()
    {
        Keccak[] keys = { TestItem.KeccakA, TestItem.KeccakB };
        GetNodeDataMessage message = new(keys);
        keys.Should().BeEquivalentTo(message.Hashes);
    }

    [Test]
    public void Throws_on_null_argument()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new GetNodeDataMessage(null));
    }

    [Test]
    public void To_string()
    {
        GetNodeDataMessage message = new(new List<Keccak>());
        _ = message.ToString();
    }

    [Test]
    public void Packet_type_and_protocol_are_correct()
    {
        Keccak[] keys = { TestItem.KeccakA, TestItem.KeccakB };
        GetNodeDataMessage message = new(keys);

        message.PacketType.Should().Be(NodeDataMessageCode.GetNodeData);
        message.Protocol.Should().Be(Protocol.NodeData);
    }


}
