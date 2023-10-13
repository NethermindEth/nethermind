// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class GetNodeDataMessageSerializerTests
    {
        private static void Test(Commitment[] keys)
        {
            GetNodeDataMessage message = new(keys);
            GetNodeDataMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip()
        {
            Commitment[] keys = { TestItem._commitmentA, TestItem._commitmentB, TestItem._commitmentC };
            Test(keys);
        }

        [Test]
        public void Roundtrip_with_nulls()
        {
            Commitment[] keys = { null, TestItem._commitmentA, null, TestItem._commitmentB, null, null };
            Test(keys);
        }
    }
}
