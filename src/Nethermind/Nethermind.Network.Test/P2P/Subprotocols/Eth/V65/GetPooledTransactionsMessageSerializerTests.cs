// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V65
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetPooledTransactionsSerializerTests
    {
        private static void Test(Hash256[] keys)
        {
            using GetPooledTransactionsMessage message = new(keys.ToPooledList());
            GetPooledTransactionsMessageSerializer serializer = new();

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

        [Test]
        public void Empty_to_string()
        {
            using GetPooledTransactionsMessage message = new(new Hash256[] { }.ToPooledList());
            _ = message.ToString();
        }
    }
}
