// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class NodeDataMessageSerializerTests
    {
        private static void Test(byte[][] data)
        {
            NodeDataMessage message = new(data);

            NodeDataMessageSerializer serializer = new();
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip()
        {
            byte[][] data = { TestItem.KeccakA.ToByteArray(), TestItem.KeccakB.ToByteArray(), TestItem.KeccakC.ToByteArray() };
            Test(data);
        }

        [Test]
        public void Zero_roundtrip()
        {
            byte[][] data = { TestItem.KeccakA.ToByteArray(), TestItem.KeccakB.ToByteArray(), TestItem.KeccakC.ToByteArray() };
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
            byte[][] data = { TestItem.KeccakA.ToByteArray(), Array.Empty<byte>(), TestItem.KeccakC.ToByteArray() };
            Test(data);
        }
    }
}
