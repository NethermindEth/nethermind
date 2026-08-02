// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V65
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class NewPooledTransactionHashesMessageSerializerTests
    {
        private static void Test(Hash256[] keys)
        {
            using NewPooledTransactionHashesMessage message = new(keys.ToPooledList());
            NewPooledTransactionHashesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip()
        {
            Hash256[] keys = [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC];
            Test(keys);
        }

        [Test]
        public void Deserialize_throws_on_null_hash()
        {
            Hash256[] keys = [null!, TestItem.KeccakA, null!, TestItem.KeccakB, null!, null!];
            using NewPooledTransactionHashesMessage message = new(keys.ToPooledList());
            NewPooledTransactionHashesMessageSerializer serializer = new();
            byte[] bytes = serializer.Serialize(message);

            Assert.That(() => serializer.Deserialize(bytes), Throws.TypeOf<RlpException>());
        }

        [Test]
        public void Empty_to_string()
        {
            using NewPooledTransactionHashesMessage message = new(ArrayPoolList<Hash256>.Empty());
            _ = message.ToString();
        }
    }
}
