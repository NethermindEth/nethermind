// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.NodeData.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.NodeData;

[Parallelizable(ParallelScope.All)]
public class GetNodeDataMessageSerializerTests
{
    private static void Test(Hash256[] keys)
    {
        GetNodeDataMessage message = new(keys.ToPooledList());
        GetNodeDataMessageSerializer serializer = new();

        SerializerTester.TestZero(serializer, message);
    }

    [Test]
    public void Roundtrip()
    {
        Hash256[] keys = { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
        Test(keys);
    }

    [Test]
    public void Roundtrip_with_nulls()
    {
        Hash256[] keys = { null, TestItem.KeccakA, null, TestItem.KeccakB, null, null };
        Test(keys);
    }
}
