// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66
{
    [TestFixture]
    public class GetPooledTransactionsSerializerTests
    {
        //test from https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
        [Test]
        public void Roundtrip()
        {
            Hash256 a = new("0x00000000000000000000000000000000000000000000000000000000deadc0de");
            Hash256 b = new("0x00000000000000000000000000000000000000000000000000000000feedbeef");
            Hash256[] keys = { a, b };

            using GetPooledTransactionsMessage message = new(keys.ToPooledList()) { RequestId = 1111 };

            GetPooledTransactionsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "f847820457f842a000000000000000000000000000000000000000000000000000000000deadc0dea000000000000000000000000000000000000000000000000000000000feedbeef");
        }
    }
}
