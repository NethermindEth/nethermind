// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class GetNodeDataMessageSerializerTests
    {
        private static void Test(Keccak[] keys)
        {
            GetNodeDataMessage message = new(keys);
            GetNodeDataMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip()
        {
            Keccak[] keys = { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            Test(keys);
        }

        [Test]
        public void Roundtrip_with_nulls()
        {
            Keccak[] keys = { null, TestItem.KeccakA, null, TestItem.KeccakB, null, null };
            Test(keys);
        }
    }
}
