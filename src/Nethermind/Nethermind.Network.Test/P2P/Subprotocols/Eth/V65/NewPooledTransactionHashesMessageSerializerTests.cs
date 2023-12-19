// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V65
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class NewPooledTransactionHashesMessageSerializerTests
    {
        private static void Test(Hash256[] keys)
        {
            NewPooledTransactionHashesMessage message = new(keys);
            NewPooledTransactionHashesMessageSerializer serializer = new();

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
            NewPooledTransactionHashesMessage message
                = new(new Hash256[] { });
            _ = message.ToString();
        }
    }
}
