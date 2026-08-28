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
            // Each pair encodes as a 34-byte-payload list (0xe2): 0xa0 + hash, then the block number.
            SerializerTester.TestZero(serializer, message,
                "f846e2a0c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc601e2a0ad7c5bef027816a800da1736444fb58a807ef4c9603b7848673f7e3a68eb14a502");
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
            Assert.That(statusMessage.ToString(), Does.StartWith(nameof(NewBlockHashesMessage)));
        }
    }
}
