// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class NewBlockHashesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            using NewBlockHashesMessage message = new((Keccak.Compute("1"), 1UL), (Keccak.Compute("2"), 2UL));
            NewBlockHashesMessageSerializer serializer = new();
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Deserialize_throws_on_null_hash()
        {
            NewBlockHashesMessageSerializer serializer = new();

            Assert.That(() => serializer.Deserialize([0xc3, 0xc2, 0x80, 0x01]), Throws.TypeOf<RlpException>());
        }

        [Test]
        public void To_string()
        {
            using NewBlockHashesMessage statusMessage = new();
            _ = statusMessage.ToString();
        }
    }
}
