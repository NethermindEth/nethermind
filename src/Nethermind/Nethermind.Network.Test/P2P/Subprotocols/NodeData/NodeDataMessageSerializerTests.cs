// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.NodeData.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.NodeData;

[Parallelizable(ParallelScope.All)]
public class NodeDataMessageSerializerTests
{
    private static void Test(ArrayPoolList<byte[]> data)
    {
        using NodeDataMessage message = new(data);

        NodeDataMessageSerializer serializer = new();
        SerializerTester.TestZero(serializer, message);
    }

    [Test]
    public void Roundtrip()
    {
        ArrayPoolList<byte[]> data = new(3) { TestItem.KeccakA.BytesToArray(), TestItem.KeccakB.BytesToArray(), TestItem.KeccakC.BytesToArray() };
        Test(data);
    }

    [Test]
    public void Zero_roundtrip()
    {
        ArrayPoolList<byte[]> data = new(3) { TestItem.KeccakA.BytesToArray(), TestItem.KeccakB.BytesToArray(), TestItem.KeccakC.BytesToArray() };
        Test(data);
    }

    [Test]
    public void Roundtrip_with_null_top_level()
    {
        Test(null);
    }

    [Test]
    public void Roundtrip_with_nulls()
    {
        ArrayPoolList<byte[]> data = new(3) { TestItem.KeccakA.BytesToArray(), Array.Empty<byte>(), TestItem.KeccakC.BytesToArray() };
        Test(data);
    }
}
